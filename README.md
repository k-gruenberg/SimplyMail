# SimplyMail

Simply a Swift library providing IMAP and SMTP client functionality for your iOS app, simply that. Works by simply wrapping existing Rust libraries. 

## Acknowledgements

I did nothing more than to combine existing libraries and tutorials, these are:

* [This tutorial](https://www.strathweb.com/2023/07/calling-rust-code-from-swift/) on calling Rust code from Swift, written by *Filip W.*, and his corresponding repository [here](https://github.com/filipw/Strathweb.Samples.RustFromSwift).
* The Rust [imap](https://crates.io/crates/imap) crate.
* The Rust [lettre](https://crates.io/crates/lettre) crate for SMTP support.

## Usage

To add this library to your Xcode project, go to "Package Dependencies", click the "+" button, enter the URL of this repository (`https://github.com/k-gruenberg/SimplyMail`) and click "Add Package".

## Building the `.xcframework` yourself

1. run `rustup update`
2. run `aarch64-apple-darwin` if necessary
3. run `rustup target add aarch64-apple-ios`
4. run `rustup target add aarch64-apple-ios-sim`
5. run `cargo install uniffi-bindgen-cs --git https://github.com/NordSecurity/uniffi-bindgen-cs --tag v0.7.0+v0.25.0`
6. run `./build.sh`

## Restrictions

The only supported platforms are iOS, the iOS simulator and macOS, corresponding to the collowing three `cargo` targets:
* `aarch64-apple-darwin`
* `aarch64-apple-ios`
* `aarch64-apple-ios-sim`
