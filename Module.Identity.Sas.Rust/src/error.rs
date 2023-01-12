use std::fmt::Display;

#[derive(Debug)]
pub(crate) struct Error {
    message: String,
    exit_code: i32,
}

impl Error {
    pub fn new(message: impl Display) -> Self {
        Error {
            message: message.to_string(),
            // The default exit code when a failure occurs.
            exit_code: 1,
        }
    }

    pub fn from_err(message: impl Display, err: impl Display) -> Self {
        Error {
            message: format!("{}: {}", message, err),
            exit_code: 1,
        }
    }
}

impl Display for Error {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.message)
    }
}

impl From<paho_mqtt::Error> for Error {
    fn from(value: paho_mqtt::Error) -> Self {
        Error { message: value.to_string(), exit_code: 2 }
    }
}