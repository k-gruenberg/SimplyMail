# SimplyMail

Simply a Swift library providing IMAP and SMTP client functionality for your iOS app, simply that. Works by simply wrapping existing Rust libraries. 

## Acknowledgements

I did nothing more than to combine existing libraries and tutorials, these are:

* [This tutorial](https://www.strathweb.com/2023/07/calling-rust-code-from-swift/) on calling Rust code from Swift, written by *Filip W.*, and his corresponding repository [here](https://github.com/filipw/Strathweb.Samples.RustFromSwift).
* The Rust [imap](https://crates.io/crates/imap) crate.
* The Rust [lettre](https://crates.io/crates/lettre) crate for SMTP support.

## Usage

To add this library to your Xcode project, go to "Package Dependencies", click the "+" button, enter the URL of this repository (`https://github.com/k-gruenberg/SimplyMail`) and click "Add Package".

## Restrictions

The only supported platforms are iOS, the iOS simulator and macOS, corresponding to the collowing three `cargo` targets:
* `aarch64-apple-darwin`
* `aarch64-apple-ios`
* `aarch64-apple-ios-sim`
