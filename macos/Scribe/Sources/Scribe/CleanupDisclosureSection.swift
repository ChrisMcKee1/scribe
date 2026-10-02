import SwiftUI

struct CleanupDisclosureSection: View {
    let providerKind: CleanupProviderKind
    let endpoint: String?
    let forceLocal: Bool

    var body: some View {
        Section("Privacy") {
            Text(CleanupDisclosure.summary(for: providerKind, endpoint: endpoint, forceLocal: forceLocal))
                .font(.caption)
                .foregroundStyle(.secondary)
            Text(CleanupDisclosure.whatCleanupSends)
                .font(.caption)
                .foregroundStyle(.secondary)
            Text(CleanupDisclosure.whatCleanupNeverSends)
                .font(.caption)
                .foregroundStyle(.secondary)
        }
    }
}
