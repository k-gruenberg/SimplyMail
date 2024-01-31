import XCTest
@testable import SimplyMail

final class SimplyMailTests: XCTestCase {
    // XCTest Documentation
    // https://developer.apple.com/documentation/xctest

    // Defining Test Cases and Test Methods
    // https://developer.apple.com/documentation/xctest/defining_test_cases_and_test_methods
    
    // cf. https://stackoverflow.com/questions/72476821/single-global-instance-for-multiple-test-cases-in-unit-testing
    // and https://developer.apple.com/documentation/xctest/xctestcase/set_up_and_tear_down_state_in_your_tests
    override class func setUp() {
        // This is the setUp() class method.
        // XCTest calls it before calling the first test method.
        // Set up any overall initial state here.
    }

    
    func testCheckImapSuccess() throws {
        try simplyCheckImap(domain: TestCredentials.IMAP_DOMAIN, port: TestCredentials.IMAP_PORT, username: TestCredentials.IMAP_USERNAME, password: TestCredentials.IMAP_PASSWORD)
    }
    
    func testCheckImapFailure() throws {
        // cf. https://stackoverflow.com/questions/32860338/how-to-unit-test-throwing-functions-in-swift
        XCTAssertThrowsError(try simplyCheckImap(domain: TestCredentials.IMAP_DOMAIN, port: TestCredentials.IMAP_PORT, username: TestCredentials.IMAP_USERNAME, password: "wrong_password")) { error in
                    //XCTAssertEqual(error as! MyError, MyError.someExpectedError)
                }
    }
    
    func testCheckSmtpSuccess() throws {
        XCTAssert(
            try simplyCheckSmtp(smtpServer: TestCredentials.SMTP_SERVER, smtpUsername: TestCredentials.SMTP_USERNAME, smtpPassword: TestCredentials.SMTP_PASSWORD)
        )
    }
    
    func testCheckSmtpFailure() throws {
        // cf. https://stackoverflow.com/questions/32860338/how-to-unit-test-throwing-functions-in-swift
        XCTAssertThrowsError(try simplyCheckSmtp(smtpServer: TestCredentials.SMTP_SERVER, smtpUsername: TestCredentials.SMTP_USERNAME, smtpPassword: "wrong_password")) { error in
                    //XCTAssertEqual(error as! MyError, MyError.someExpectedError)
                }
    }
}
