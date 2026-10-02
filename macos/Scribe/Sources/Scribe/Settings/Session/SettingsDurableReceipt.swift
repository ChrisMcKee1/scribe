import Foundation

/// A replay witness contains identities, not another permanent copy of deleted dictionary/snippet text or secrets.
struct SettingsDurableReceipt: Codable, Sendable {
    let id: UUID
    let revision: UInt64
    let rowIDs: [String: Int64]
    let attachmentKeys: [String]

    init(_ receipt: SettingsCommitReceipt) {
        id = receipt.id
        revision = receipt.revision
        rowIDs = receipt.rowIDs
        attachmentKeys = receipt.attachment.values.keys.sorted()
    }

    private enum CodingKeys: String, CodingKey {
        case id, revision, rowIDs, attachmentKeys, attachment
    }

    init(from decoder: any Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        id = try container.decode(UUID.self, forKey: .id)
        revision = try container.decode(UInt64.self, forKey: .revision)
        rowIDs = try container.decodeIfPresent([String: Int64].self, forKey: .rowIDs) ?? [:]
        if let keys = try container.decodeIfPresent([String].self, forKey: .attachmentKeys) {
            attachmentKeys = keys
        } else {
            let old = try container.decodeIfPresent(SettingsCommitAttachment.self, forKey: .attachment)
            attachmentKeys = old?.values.keys.sorted() ?? []
        }
    }

    func encode(to encoder: any Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode(id, forKey: .id)
        try container.encode(revision, forKey: .revision)
        try container.encode(rowIDs, forKey: .rowIDs)
        try container.encode(attachmentKeys, forKey: .attachmentKeys)
    }

    func receipt(document: SettingsDocument, attachment: SettingsCommitAttachment) -> SettingsCommitReceipt {
        var receipt = SettingsCommitReceipt(id: id, revision: revision, document: document)
        receipt.rowIDs = rowIDs
        receipt.attachment = attachment
        return receipt
    }
}
