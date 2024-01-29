uniffi::include_scaffolding!("rust-lib");

// ***** IMAP: *****

extern crate imap;
extern crate native_tls;

// cf. https://crates.io/crates/imap/2.4.1
pub fn fetch_inbox_top(domain: &str, port: u16, username: &str, password: &str) { // -> imap::error::Result<Option<String>>
    //let domain = "imap.example.com";
    let tls = native_tls::TlsConnector::builder().build().unwrap();

    // we pass in the domain twice to check that the server's TLS
    // certificate is valid for the domain we're connecting to.
    let client = imap::connect((domain, port), domain, &tls).unwrap();

    // the client we have here is unauthenticated.
    // to do anything useful with the e-mails, we need to log in
    let mut imap_session = client
        .login(username, password) // .login("me@example.com", "password")
        .map_err(|e| e.0).unwrap(); //.map_err(|e| e.0)?;

    // we want to fetch the first email in the INBOX mailbox
    imap_session.select("INBOX").unwrap(); // imap_session.select("INBOX")?;

    // fetch message number 1 in this mailbox, along with its RFC822 field.
    // RFC 822 dictates the format of the body of e-mails
    let messages = imap_session.fetch("1", "RFC822").unwrap(); // let messages = imap_session.fetch("1", "RFC822")?;
    let message = messages.iter().next().unwrap(); /*let message = if let Some(m) = messages.iter().next() {
        m
    } else {
        return Ok(None);
    };*/

    // extract the message's body
    let body = message.body().expect("message did not have a body!");
    let body = std::str::from_utf8(body)
        .expect("message was not valid utf-8")
        .to_string();

    // be nice to the server and log out
    imap_session.logout().unwrap(); // imap_session.logout()?;

    //Ok(Some(body))
}

// ***** SMTP: *****

use lettre::message::header::ContentType;
use lettre::transport::smtp::authentication::Credentials;
use lettre::{Message, SmtpTransport, Transport};

// cf. https://crates.io/crates/lettre
pub fn send_plain_text_email(from: &str, reply_to: &str, to: &str, subject: &str, body: &str,
	smtp_server: &str, smtp_username: &str, smtp_password: &str) {
	let email = Message::builder()
	    .from(from.parse().unwrap()) //.from("NoBody <nobody@domain.tld>".parse().unwrap())
	    .reply_to(reply_to.parse().unwrap()) //.reply_to("Yuin <yuin@domain.tld>".parse().unwrap())
	    .to(to.parse().unwrap()) //.to("Hei <hei@domain.tld>".parse().unwrap())
	    .subject(subject) //.subject("Happy new year")
	    .header(ContentType::TEXT_PLAIN)
	    .body(String::from(body)) //.body(String::from("Be happy!"))
	    .unwrap();

	//let creds = Credentials::new("smtp_username".to_owned(), "smtp_password".to_owned());
	let creds = Credentials::new(smtp_username.to_owned(), smtp_password.to_owned());

	// Open a remote connection to gmail
	let mailer = SmtpTransport::relay(smtp_server) //let mailer = SmtpTransport::relay("smtp.gmail.com")
	    .unwrap()
	    .credentials(creds)
	    .build();

	// Send the email
	match mailer.send(&email) {
	    Ok(_) => println!("Email sent successfully!"),
	    Err(e) => panic!("Could not send email: {e:?}"),
	}
}
