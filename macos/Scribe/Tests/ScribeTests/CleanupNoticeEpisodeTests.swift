import XCTest

@testable import Scribe

final class CleanupNoticeEpisodeTests: XCTestCase {
    func testFailureIsReportedOnceUntilCleanupRecovers() {
        var episode = CleanupNoticeEpisode()

        XCTAssertTrue(episode.apply(.failed))
        XCTAssertFalse(episode.apply(.failed))
        XCTAssertFalse(episode.apply(.recovered))
        XCTAssertTrue(episode.apply(.failed))
    }

    func testConfigurationChangeStartsANewNoticeEpisode() {
        var episode = CleanupNoticeEpisode()

        XCTAssertTrue(episode.apply(.failed))
        XCTAssertFalse(episode.apply(.configurationChanged))
        XCTAssertTrue(episode.apply(.failed))
    }
}
