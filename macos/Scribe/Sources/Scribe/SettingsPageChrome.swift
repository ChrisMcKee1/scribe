import SwiftUI

struct SettingsPage<Content: View>: View {
    let title: String
    let subtitle: String
    @ViewBuilder let content: () -> Content

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            VStack(alignment: .leading, spacing: 6) {
                Text(title)
                    .font(.system(size: 28, weight: .semibold))
                Text(subtitle)
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            .frame(maxWidth: .infinity, alignment: .leading)

            content()
        }
        .frame(maxWidth: 1_000, alignment: .leading)
    }
}

struct SettingsGroupHeader: View {
    let text: String

    init(_ text: String) {
        self.text = text
    }

    var body: some View {
        Text(text)
            .font(.headline.weight(.semibold))
            .padding(.top, 4)
            .frame(maxWidth: .infinity, alignment: .leading)
    }
}

struct SettingsCard<Content: View>: View {
    let searchID: String?
    @Environment(\.settingsSearchHighlightID) private var highlightedSearchID
    @ViewBuilder let content: () -> Content

    init(searchID: String? = nil, @ViewBuilder content: @escaping () -> Content) {
        self.searchID = searchID
        self.content = content
    }

    var body: some View {
        if let searchID {
            card.id(searchID)
        } else {
            card
        }
    }

    private var card: some View {
        content()
            .padding(14)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(
                RoundedRectangle(cornerRadius: 10, style: .continuous)
                    .fill(Color(nsColor: .controlBackgroundColor))
            )
            .overlay(
                RoundedRectangle(cornerRadius: 10, style: .continuous)
                    .stroke(borderColor, lineWidth: borderWidth)
            )
    }

    private var borderColor: Color {
        if searchID == highlightedSearchID {
            return Color.accentColor
        }
        return Color(nsColor: .separatorColor).opacity(0.35)
    }

    private var borderWidth: CGFloat {
        searchID == highlightedSearchID ? 2 : 1
    }
}

struct CardTitle: ViewModifier {
    func body(content: Content) -> some View {
        content.font(.body.weight(.semibold))
    }
}

struct CardDescription: ViewModifier {
    func body(content: Content) -> some View {
        content
            .font(.caption)
            .foregroundStyle(.secondary)
            .fixedSize(horizontal: false, vertical: true)
    }
}

extension View {
    func cardTitle() -> some View { modifier(CardTitle()) }
    func cardDescription() -> some View { modifier(CardDescription()) }
}
