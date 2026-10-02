import SwiftUI

struct SettingsAdvancedPage: View {
    var body: some View {
        SettingsPage(
            title: "Advanced",
            subtitle: "Settings most people never need to change. The defaults suit most Macs."
        ) {
            SettingsGroupHeader("Advanced settings")
            SettingsCard {
                VStack(alignment: .leading, spacing: 8) {
                    Text("No advanced controls yet").cardTitle()
                    Text(
                        "The macOS app does not yet expose the Windows advanced controls for speech model, "
                            + "silence trimming, longest recording, typing method, line breaks, text changes or "
                            + "memory release. Those features keep their current defaults."
                    )
                    .cardDescription()
                }
            }
        }
    }
}
