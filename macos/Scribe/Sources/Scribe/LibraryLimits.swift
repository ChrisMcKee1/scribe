import Foundation

enum LibraryLimits {
    static let maxFieldLength = 2_000
    static let maxTermsPerLibrary = 50_000
    static let maxImportBytes: Int = 10 * 1024 * 1024
}
