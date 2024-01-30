uniffi::include_scaffolding!("rust-lib");

use thiserror::Error;
use std::collections::HashMap;
use std::net::TcpStream;

// ***** IMAP: *****

extern crate imap;
use imap::Session;

extern crate native_tls;
use native_tls::TlsStream;

// A simplified wrapper for imap::error::Error
// cf. https://docs.rs/imap/2.4.1/imap/error/enum.Error.html
#[derive(Error, Debug)]
pub enum ImapError {
	#[error("IMAP error: an IO error occurred while trying to read or write to a network stream.")]
	IoError, // Io(IoError),
	#[error("IMAP error: an error from the native_tls library during the TLS handshake.")]
    TlsHandshakeError, // TlsHandshake(TlsHandshakeError<TcpStream>),
    #[error("IMAP error: an error from the native_tls library while managing the socket.")]
    TlsError, // Tls(TlsError)
    #[error("IMAP error: A BAD response from the IMAP server.")]
    BadResponse, // Bad(String),
    #[error("IMAP error: A NO response from the IMAP server.")]
    NoResponse, //No(String),
    #[error("IMAP error: The connection was terminated unexpectedly.")]
    ConnectionLost,
    #[error("IMAP error: Error parsing a server response.")]
    ParseError, // Parse(ParseError),
    #[error("IMAP error: Command inputs were not valid IMAP strings.")]
    ValidateError, // Validate(ValidateError),
    #[error("IMAP error: Error appending an e-mail.")]
    AppendError,
    #[error("Undefined IMAP error.")]
    __Nonexhaustive,
}

impl From<imap::Error> for ImapError {
    fn from(error: imap::Error) -> Self {
        match error {
        	imap::Error::Io(_) => Self::IoError,
		    imap::Error::TlsHandshake(_) => Self::TlsHandshakeError,
		    imap::Error::Tls(_) => Self::TlsError,
		    imap::Error::Bad(_) => Self::BadResponse,
		    imap::Error::No(_) => Self::NoResponse,
		    imap::Error::ConnectionLost => Self::ConnectionLost,
		    imap::Error::Parse(_) => Self::ParseError,
		    imap::Error::Validate(_) => Self::ValidateError,
		    imap::Error::Append => Self::AppendError,
		    imap::Error::__Nonexhaustive => Self::__Nonexhaustive,
        }
    }
}

fn get_imap_session(domain: &str, port: u16, username: &str, password: &str) -> Result<Session<TlsStream<TcpStream>>, imap::Error> {
	//let domain = "imap.example.com";
    let tls = native_tls::TlsConnector::builder().build().unwrap();

    // we pass in the domain twice to check that the server's TLS
    // certificate is valid for the domain we're connecting to.
    let client = imap::connect((domain, port), domain, &tls).unwrap();

    // the client we have here is unauthenticated.
    // to do anything useful with the e-mails, we need to log in
    let imap_session = client
        .login(username, password) // .login("me@example.com", "password")
        // .login() returns a Result<Session<T>, (imap::Error, Client<T>)>
        .map_err(|e| e.0);

    return imap_session
}

// cf. https://github.com/jonhoo/rust-imap/blob/main/examples/gmail_oauth2.rs
struct GmailOAuth2 {
    user: String,
    access_token: String,
}
impl imap::Authenticator for GmailOAuth2 {
    type Response = String;
    #[allow(unused_variables)]
    fn process(&self, data: &[u8]) -> Self::Response {
        format!(
            "user={}\x01auth=Bearer {}\x01\x01",
            self.user, self.access_token
        )
    }
}
fn get_imap_session_gmail_oauth2(username: &str, access_token: &str) -> Result<Session<TlsStream<TcpStream>>, imap::Error> {
    let tls = native_tls::TlsConnector::builder().build().unwrap();

    //let client = imap::ClientBuilder::new("imap.gmail.com", 993).connect().expect("Could not connect to imap.gmail.com");
    let domain = "imap.gmail.com";
    let port = 993;
    // we pass in the domain twice to check that the server's TLS
    // certificate is valid for the domain we're connecting to.
    let client = imap::connect((domain, port), domain, &tls).unwrap();

	let gmail_auth = GmailOAuth2 {
	    user: String::from(username), //user: String::from("sombody@gmail.com"),
	    access_token: String::from(access_token), //access_token: String::from("<access_token>"),
    };

    let imap_session = client.authenticate("XOAUTH2", &gmail_auth)
    	.map_err(|e| e.0);

    return imap_session
}

// cf. https://crates.io/crates/imap/2.4.1
pub fn simply_fetch_inbox_top(domain: &str, port: u16, username: &str, password: &str) -> Result<Option<String>, ImapError> { // -> imap::error::Result<Option<String>>
    let mut imap_session = get_imap_session(domain, port, username, password)?;

    // we want to fetch the first email in the INBOX mailbox
    imap_session.select("INBOX")?;

    // fetch message number 1 in this mailbox, along with its RFC822 field.
    // RFC 822 dictates the format of the body of e-mails
    let messages = imap_session.fetch("1", "RFC822")?;
    let message = if let Some(m) = messages.iter().next() {
        m
    } else {
        return Ok(None);
    };

    // extract the message's body
    let body = message.body().expect("message did not have a body!");
    let body = std::str::from_utf8(body)
        .expect("message was not valid utf-8")
        .to_string();

    // be nice to the server and log out
    imap_session.logout()?;

    Ok(Some(body))
}

// ***** SMTP: *****

use lettre::message::header::ContentType;
use lettre::transport::smtp::authentication::Credentials;
use lettre::{Message, SmtpTransport, Transport};
use lettre::message::MultiPart;

// A simplified wrapper for the `Kind` in the `Inner` struct stored inside a lettre::transport::smtp::Error
// cf. https://docs.rs/lettre/latest/src/lettre/transport/smtp/error.rs.html
#[derive(Error, Debug)]
pub enum SmtpError {
    #[error("SMTP error: Transient SMTP error, 4xx reply code [RFC 5321, section 4.2.1].")]
    TransientSmtpError,
    #[error("SMTP error: Permanent SMTP error, 5xx reply code [RFC 5321, section 4.2.1].")]
    PermanentSmtpError,
    #[error("SMTP error: Error parsing a response.")]
    ResponseParseError,
    #[error("SMTP error: Internal client error.")]
    InternalClientError,
    #[error("SMTP error: Connection error.")]
    ConnectionError,
    #[error("SMTP error: Underlying network i/o error.")]
    NetworkError,
    #[error("SMTP error: TLS error.")]
    TlsError,

    #[error("SMTP error: Timeout.")]
    Timeout,
    #[error("SMTP error: other.")]
    OtherError,
}

impl From<lettre::transport::smtp::Error> for SmtpError {
    fn from(error: lettre::transport::smtp::Error) -> Self {
    	if error.is_response() {
    		Self::ResponseParseError
    	} else if error.is_client() {
    		Self::InternalClientError
    	} else if error.is_transient() {
    		Self::TransientSmtpError
    	} else if error.is_permanent() {
    		Self::PermanentSmtpError
    	} else if error.is_tls() {
    		Self::TlsError
    	} else if error.is_timeout() {
    		Self::Timeout
    	} else if error.to_string().to_lowercase().contains("network error") {
    		Self::NetworkError
    	} else if error.to_string().to_lowercase().contains("connection error") {
    		Self::ConnectionError
    	} else {
    		Self::OtherError
    	}
    }
}

// represents a lettre::transport::smtp::response::Response, including its lettre::transport::smtp::response::Code
pub struct SmtpResponse {
	pub severity: u8,
    pub category: u8,
    pub detail: u8,
    pub message: String,
}

impl From<lettre::transport::smtp::response::Response> for SmtpResponse {
    fn from(response: lettre::transport::smtp::response::Response) -> Self {
    	// cf. https://docs.rs/lettre/0.11.4/lettre/transport/smtp/response/struct.Response.html
    	SmtpResponse {
    		severity: response.code().severity as u8, // cf. https://stackoverflow.com/questions/31358826/how-do-i-convert-an-enum-reference-to-a-number
		    category: response.code().category as u8,
		    detail: response.code().detail as u8,
		    message: response.message().collect::<Vec<&str>>().join("\n"),
    	}
    }
}

fn prepare_headers(headers: HashMap<String, String>) -> lettre::message::MessageBuilder {
	let mut email = Message::builder();

	for (header, value) in headers {
		// cf. https://docs.rs/lettre/latest/lettre/message/struct.MessageBuilder.html
		match header.as_ref() {
			"From" => email = email.from(value.parse().unwrap()), //.from("NoBody <nobody@domain.tld>".parse().unwrap())
			"Sender" => email = email.sender(value.parse().unwrap()), // "Should be used when providing several From mailboxes."
			"Reply-To" => email = email.reply_to(value.parse().unwrap()), //.reply_to("Yuin <yuin@domain.tld>".parse().unwrap())
			"To" => email = email.to(value.parse().unwrap()), //.to("Hei <hei@domain.tld>".parse().unwrap())
			"Cc" => email = email.cc(value.parse().unwrap()),
			"Bcc" => email = email.bcc(value.parse().unwrap()),
			"In-Reply-To" => email = email.in_reply_to(value),
			"References" => email = email.references(value),
			"Subject" => email = email.subject(value), //.subject("Happy new year")
			"User-Agent" => email = email.user_agent(value), // https://datatracker.ietf.org/doc/html/draft-melnikov-email-user-agent-00
			other => panic!("Unknwon email header: '{other}'")
		}
	}

	return email
}

fn send_email(smtp_server: &str, smtp_username: &str, smtp_password: &str,
	email: lettre::Message) -> Result<SmtpResponse, SmtpError> {
	//let creds = Credentials::new("smtp_username".to_owned(), "smtp_password".to_owned());
	let creds = Credentials::new(smtp_username.to_owned(), smtp_password.to_owned());

	// Open a remote connection to gmail
	let mailer = SmtpTransport::relay(smtp_server) //let mailer = SmtpTransport::relay("smtp.gmail.com")
	    .unwrap()
	    .credentials(creds)
	    .build();

	// Send the email
	return match mailer.send(&email) {
	    Ok(response) => Ok(response.into()), //println!("Email sent successfully!"), // lettre::transport::smtp::response::Response
	    Err(e) => Err(e.into()) //panic!("Could not send email: {e:?}"), // lettre::transport::smtp::Error
	}
}

// cf. https://crates.io/crates/lettre
pub fn simply_send_plain_text_email(smtp_server: &str, smtp_username: &str, smtp_password: &str,
	headers: HashMap<String, String>, body: &str) -> Result<SmtpResponse, SmtpError> {
	let email = prepare_headers(headers)
		.header(ContentType::TEXT_PLAIN)
		.body(String::from(body)) //.body(String::from("Be happy!"))
	    .unwrap();

	return send_email(smtp_server, smtp_username, smtp_password, email)
}

// cf. https://docs.rs/lettre/latest/lettre/message/index.html
pub fn simply_send_html_email(smtp_server: &str, smtp_username: &str, smtp_password: &str,
	headers: HashMap<String, String>, plain_text_body: &str, html_body: &str) -> Result<SmtpResponse, SmtpError> {
	let email = prepare_headers(headers)
		.multipart(MultiPart::alternative_plain_html(
	        String::from(plain_text_body), //String::from("Hello, world! :)"),
	        String::from(html_body), //String::from("<p><b>Hello</b>, <i>world</i>! <img src=\"cid:123\"></p>"),
	    ))
	    .unwrap();

	return send_email(smtp_server, smtp_username, smtp_password, email)
}
