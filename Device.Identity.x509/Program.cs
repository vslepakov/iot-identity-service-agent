using Microsoft.Azure.Devices.Client;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Device.Identity.x509
{
    class Program
    {
        static async Task Main(string[] args)
        {
            var cts = new CancellationTokenSource();
            var cancellationToken = cts.Token;
            AssemblyLoadContext.Default.Unloading += (ctx) => cts.Cancel();
            Console.CancelKeyPress += (sender, cpe) => cts.Cancel();

            var identityInfo = await GetIdentityInfoAsync(cancellationToken);
            var auth = await GetIdCertWithPrivateKeyHandle(identityInfo, cancellationToken);
            await StartSendingDataAsync(identityInfo, auth, cancellationToken);

            WhenCancelled(cancellationToken).Wait();
        }

        // Step 1 Get identity information 
        private static async Task<IdentityInfo> GetIdentityInfoAsync(CancellationToken cancellationToken)
        {
            Console.WriteLine("Getting Identity Info...");

            using var idHttpClient = new HttpClient(new SocketsHttpHandler
            {
                ConnectCallback = async (context, token) =>
                {
                    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.IP);
                    var endpoint = new UnixDomainSocketEndPoint("/run/aziot/identityd.sock");
                    await socket.ConnectAsync(endpoint, cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
            });
            var json = await idHttpClient.GetStringAsync(@"http://identityd.sock/identities/identity?api-version=2020-09-01", cancellationToken);
            Console.WriteLine($"Identity Info: {json}");

            var jObject = JObject.Parse(json);
            return JsonConvert.DeserializeObject<IdentityInfo>(jObject["spec"].ToString());
        }

        // Step 2 Get x509 Auth using a private key handle aquired through "aziot_keys" openssl engine (potentially backed by HSM)
        private static async Task<DeviceAuthenticationWithX509Certificate> GetIdCertWithPrivateKeyHandle(IdentityInfo identityInfo, CancellationToken cancellationToken)
        {
            Console.WriteLine("Getting Identity certificate...");

            using var certdHttpClient = new HttpClient(new SocketsHttpHandler
            {
                ConnectCallback = async (context, token) =>
                {
                    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.IP);
                    var endpoint = new UnixDomainSocketEndPoint("/run/aziot/certd.sock");
                    await socket.ConnectAsync(endpoint, cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
            });
            var json = await certdHttpClient.GetStringAsync($"http://certd.sock/certificates/{identityInfo.Auth.CertId}?api-version=2020-09-01", cancellationToken);
            var pem = JObject.Parse(json)["pem"].ToString();

            var pattern = @"(?<=-----BEGIN CERTIFICATE-----)(?:\S+|\s(?!-----END CERTIFICATE-----))+(?=\s-----END CERTIFICATE-----)";
            var rg = new Regex(pattern);
            var matches = rg.Matches(pem);

            Console.WriteLine($"Matches: {matches.Count}");

            var certChain = new X509Certificate2Collection();
            X509Certificate2 deviceCert = null;

            for (int count = 0; count < matches.Count; count++)
            {
                var x509 = new X509Certificate2(Convert.FromBase64String(matches[count].Value));
                var basicConstraintExt = x509.Extensions["2.5.29.19"] as X509BasicConstraintsExtension;

                if (basicConstraintExt != null && basicConstraintExt.CertificateAuthority)
                {
                    Console.WriteLine($"CA cert: {matches[count].Value}");
                    certChain.Add(x509);
                }
                else
                {
                    Console.WriteLine($"Device ID cert: {matches[count].Value}");

                    var pkeyHandle = GetPrivateKeyHandle(identityInfo);
#pragma warning disable CA1416 // Validate platform compatibility
                    deviceCert = x509.CopyWithPrivateKey(new RSAOpenSsl(pkeyHandle));
#pragma warning restore CA1416 // Validate platform compatibility
                }
            }

            return new DeviceAuthenticationWithX509Certificate(identityInfo.DeviceId, deviceCert, certChain);
        }

        // Step 3: Start sending data to IoT Hub
        private static async Task StartSendingDataAsync(IdentityInfo identityInfo, DeviceAuthenticationWithX509Certificate auth,
            CancellationToken cancellationToken)
        {
            Console.WriteLine($"Connecting to IoT Hub {identityInfo.HubName} using x509 auth as Device {identityInfo.DeviceId}...");
            using var deviceClient = DeviceClient.Create(identityInfo.HubName, auth, TransportType.Mqtt_Tcp_Only);

            await deviceClient.OpenAsync(cancellationToken);
            Console.WriteLine("Device connection SUCCESS.");

            var dataGenerator = new DataGenerator.DataGenerator();
            await dataGenerator.GenerateDataAsync(
                (message, cancellationToken) => deviceClient.SendEventAsync(message, cancellationToken), cancellationToken);
        }

        private static Task WhenCancelled(CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<bool>();
            cancellationToken.Register(s => ((TaskCompletionSource<bool>)s).SetResult(true), tcs);
            return tcs.Task;
        }

        private static SafeEvpPKeyHandle GetPrivateKeyHandle(IdentityInfo identityInfo)
        {
            var engine = NativeMethods.ENGINE_by_id("aziot_keys");
            _ = NativeMethods.ENGINE_init(engine);
            var pkey = NativeMethods.ENGINE_load_private_key(engine, identityInfo.Auth.KeyHandle, IntPtr.Zero, IntPtr.Zero);

#pragma warning disable CA1416 // Validate platform compatibility
            Console.WriteLine($"OpenSSL version: {SafeEvpPKeyHandle.OpenSslVersion}");

            var pkeyHandle = new SafeEvpPKeyHandle(pkey, true);
            if (pkeyHandle.IsInvalid)
#pragma warning restore CA1416 // Validate platform compatibility
            {
                throw new InvalidOperationException($"Engine: unable to find private key with handle: {identityInfo.Auth.KeyHandle}");
            }

            return pkeyHandle;
        }
    }
}
