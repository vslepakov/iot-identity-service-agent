use aziot_key_common::KeyHandle;
use error::Error;
use paho_mqtt as mqtt;
use std::time::Duration;
use std::{ops::Add, time::SystemTime};

mod error;

#[tokio::main]
async fn main() {
    env_logger::init();

    let identity_info = get_identity_info()
        .await
        .expect("there should be a module identity!");

    log::info!(
        "Device is {} on {} with module id {:#?}",
        identity_info.device_id.0,
        identity_info.hub_name,
        identity_info.module_id
    );

    let module_id = identity_info.module_id.and_then(|id| Some(id.0)).unwrap();

    let key_handle = identity_info
        .auth
        .and_then(|auth| Some(auth.key_handle).and_then(|kh| kh))
        .unwrap();

    let sas_token = get_sas_token(
        &identity_info.hub_name,
        &identity_info.device_id.0,
        &module_id,
        &key_handle,
    )
    .await
    .unwrap();

    log::info!("SAS Token: {sas_token}");

    let mqtt_client = get_mqtt_client(
        &identity_info.hub_name,
        &identity_info.device_id.0,
        &module_id,
        &sas_token,
    )
    .await
    .unwrap();

    send_data(&mqtt_client, &identity_info.device_id.0, &module_id)
        .await
        .unwrap();
}

async fn get_identity_info() -> Result<aziot_identity_common::AzureIoTSpec, Error> {
    let uri = url::Url::parse("unix:///run/aziot/identityd.sock")
        .expect("cannot fail to parse hardcoded url");

    let identity_connector = http_common::Connector::new(&uri).unwrap();

    let identity_client = aziot_identity_client_async::Client::new(
        aziot_identity_common_http::ApiVersion::V2020_09_01,
        identity_connector,
        1,
    );

    log::info!("Obtaining Edge device provisioning data...");

    match identity_client.get_caller_identity().await {
        Ok(identity_info) => match identity_info {
            aziot_identity_common::Identity::Aziot(identity_info) => {
                return Ok(identity_info);
            }
            aziot_identity_common::Identity::Local(..) => {
                // Identity Service should never return an invalid device identity.
                // Treat this as a fatal error.
                return Err(Error::new("Invalid device identity"));
            }
        },
        Err(err) => {
            return Err(Error::from_err("Failed to obtain device identity", err));
        }
    }
}

async fn get_sas_token(
    hub_name: &str,
    device_id: &str,
    module_id: &str,
    key_handle: &KeyHandle,
) -> Result<String, Error> {
    let resource_uri = get_resource_uri(&hub_name, &device_id, &module_id);
    let expiry = SystemTime::now()
        .add(Duration::new(50000, 0))
        .duration_since(SystemTime::UNIX_EPOCH)
        .expect("the time is correctly set");

    let data_to_sign = format!("{}\n{}", resource_uri, expiry.as_secs());

    let uri =
        url::Url::parse("unix:///run/aziot/keyd.sock").expect("cannot fail to parse hardcoded url");

    let keyd_connector = http_common::Connector::new(&uri).unwrap();

    let keyd_client = aziot_key_client_async::Client::new(
        aziot_key_common_http::ApiVersion::V2020_09_01,
        keyd_connector,
        1,
    );

    log::info!("Creating SAS token signature...");

    match keyd_client
        .sign(
            &key_handle,
            aziot_key_common::SignMechanism::HmacSha256,
            &data_to_sign.as_bytes(),
        )
        .await
    {
        Ok(signature) => {
            let base64_encoded = base64::encode(&signature);
            let base64_uri_encoded = urlencoding::encode(&base64_encoded);
            let sas_token = format!(
                "sr={}&se={}&sig={}",
                resource_uri,
                expiry.as_secs(),
                base64_uri_encoded.to_string()
            );
            let sas = format!("SharedAccessSignature {sas_token}");
            return Ok(sas);
        }
        Err(err) => {
            return Err(Error::from_err("Failed to sign digest", err));
        }
    }
}

async fn send_data(cli: &mqtt::AsyncClient, device_id: &str, module_id: &str) -> Result<(), Error> {
    let topic = format!(
        "devices/{}/modules/{}/messages/events/$.ct=application%2Fjson%3Bcharset%3Dutf-8",
        device_id, module_id
    );

    log::info!("Publishing data on the topic '{}'", topic);

    let msg = mqtt::MessageBuilder::new()
        .payload(format!(
            "{{\"message\": \"Hello from {}/{}\"}}",
            device_id, module_id
        ))
        .qos(1)
        .topic(topic)
        .finalize();

    match cli.try_publish(msg) {
        Ok(tok) => {
            if let Err(err) = tok.await {
                return Err(Error::from_err("Error sending message:", err));
            }

            log::info!("Successfully send data to IoT Hub");
            cli.disconnect(None).await?;
            return Ok(());
        }
        Err(err) => {
            return Err(Error::from_err("Error sending message:", err));
        }
    }
}

async fn get_mqtt_client(
    hub_name: &str,
    device_id: &str,
    module_id: &str,
    sas_token: &str,
) -> Result<mqtt::AsyncClient, Error> {
    let create_opts = mqtt::CreateOptionsBuilder::new()
        .server_uri(format!("ssl://{}:8883", hub_name))
        .client_id(format!("{}/{}", device_id, module_id))
        .mqtt_version(4)
        .persistence(mqtt::PersistenceType::None)
        .finalize();

    let cli = mqtt::AsyncClient::new(create_opts)?;

    let ssl_options = mqtt::SslOptionsBuilder::new()
        .trust_store("Baltimore.pem")?
        .finalize();

    let connect_opts = mqtt::ConnectOptionsBuilder::new()
        .user_name(format!(
            "{}/{}/{}/?api-version=2021-04-12",
            hub_name, device_id, module_id
        ))
        .password(sas_token)
        .ssl_options(ssl_options)
        .finalize();

    cli.connect(connect_opts).await?;
    log::info!("Connected to IoT Hub");

    return Ok(cli);
}

fn get_resource_uri(hub_name: &str, device_id: &str, module_id: &str) -> String {
    let uri = format!("{}/devices/{}/modules/{}", hub_name, device_id, module_id);

    return urlencoding::encode(&uri).to_string();
}
