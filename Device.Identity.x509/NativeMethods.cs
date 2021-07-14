using System;
using System.Runtime.InteropServices;

namespace Device.Identity.x509
{
    internal static class NativeMethods
    {
        private const string SSLLIB = "/lib/arm-linux-gnueabihf/libssl.so";

        [DllImport(SSLLIB, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr ENGINE_load_private_key(IntPtr engine_impl, string keyId, IntPtr ui_method, IntPtr callback_data);

        [DllImport(SSLLIB, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr ENGINE_by_id(string id);

        [DllImport(SSLLIB, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ENGINE_init(IntPtr engine_impl);
    }
}
