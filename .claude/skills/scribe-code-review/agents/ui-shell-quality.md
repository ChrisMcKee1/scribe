# UI shell quality review lens

You answer the question `core-app-layering` deliberately does not: **that logic stayed out of the
code-behind is settled, but is the UI itself correct, consistent, and usable?** Does the change speak
the WPF-UI Fluent vocabulary this shell already speaks, follow the system theme without ever forcing
one, stay reachable by keyboard, keep meaning readable without colour, honour the Windows
reduced-motion setting, and stay inside the brand PRODUCT.md describes?

Scribe is a Windows tray app, not a web app. The shell is a WPF window vocabulary from a single
package, plus one always-on-top floating surface that deliberately does not follow the theme at all.
Half the rules below exist because a UI shortcut here is invisible to the compiler and to the test
suite: `tests/Scribe.Core.Tests/Scribe.Core.Tests.csproj` references only `Scribe.Core`, so nothing
in this lens's blast radius has a test that can fail.

**Dispatch trigger.** The diff touches `*.xaml`, `*.xaml.cs`, `src/Scribe.App/Tray/**`,
`src/Scribe.App/Onboarding/**`, `src/Scribe.App/QuickAdd/**`, or `src/Scribe.App/Settings/**`. In
practice that covers all four WPF windows under `src/Scribe.App/`, the tray menu host, and
`src/Scribe.Overlay/OverlayWindow.xaml(.cs)` and `App.xaml(.cs)`.

**Severity cap:** 🟡 Important. **Findings cap:** 5.

There is no 🔴 in this lens, and that is deliberate. A UI change that is genuinely 🔴 belongs to
another lens: pill process contract and `AllowsTransparency` to `overlay-process-contract`, a decision
landing in a `.xaml.cs` to `core-app-layering`, a transcript rendered into a log line to
`privacy-egress`. Raise your half at 🟡 with a one-line cross-reference and let synthesis dedup.

**Data on disk.** Read `diff.patch` (and `delta.patch` on a re-review) plus `metadata.json` from the
cache. The reviewed branch may not be checked out, so `diff.patch` is authoritative for what changed.
Use Read and Grep freely for surrounding context: the sibling window, the existing style dictionary,
the `why` comments. Several of these XAML files carry long comments recording the exact incident a
shape prevents, and this lens is worthless without reading them.

**Staleness guard.** Line numbers below were correct when this lens was written and XAML files drift
fast. The **symbol name is the anchor**: grep for the style key, the `x:Name`, or the method name
before citing it in a finding. A dead citation in a review is worse than no citation.

---

## §0. Evidence map before any verdict

Before you flag or clear a UI change, be able to name all six of these. If one is missing, say the gap
instead of concluding.

1. **Which surface it is.** A `ui:FluentWindow` chrome surface (Settings, Quick add, Welcome,
   Dictionary cleanup), the fixed-dark floating surface (the WinUI pill), the tray context menu, or
   the overlay process. The theme and colour rules differ per surface and §6 is the scoping
   section.
2. **Which framework.** `src/Scribe.App/**` is WPF plus WPF-UI 4.3.0 (`Directory.Packages.props:41`,
   package id `WPF-UI`, namespace `Wpf.Ui`). `src/Scribe.Overlay/**` is WinUI 3 with **no** WPF-UI and
   no `Scribe.Core` reference. A WPF API name in an overlay finding is an instant false positive.
3. **What the existing sibling does.** Name the window that already solves the same problem and what
   control it used. If you cannot, you are reviewing against your own taste rather than this codebase.
4. **Whether the diff added it.** Pre-existing markup the diff merely reindented, moved, or read past
   is out of scope. This lens reviews what changed.
5. **What the user loses if you are right.** Every rule here maps to a symptom: a control nobody can
   reach with the keyboard, a window that stays dark when Windows went light, a surface that animates
   forever for someone who turned animations off, a state only distinguishable by colour.
6. **Which lens owns it.** See the cap note above.

If you cannot name 3 and 5, do not flag. "This could be nicer" is exactly the noise this lens exists
to prevent. Raise it as a Question or stay silent.

---

## §1. WPF-UI Fluent controls are the house vocabulary

`src/Scribe.App/App.xaml:6-13` merges `ui:ThemesDictionary` and `ui:ControlsDictionary` at the
application level. That has two consequences and confusing them produces bad findings.

**The `ui:` types are the vocabulary for anything WPF-UI ships its own control for.** Verified in-repo
usage across the four App XAML files: `ui:FluentWindow` (every window),
`ui:TitleBar`, `ui:Card`, `ui:CardExpander`, `ui:Button`, `ui:TextBox`, `ui:PasswordBox`,
`ui:ToggleSwitch`, `ui:SymbolIcon`, `ui:InfoBar`, `ui:Badge`, `ui:ProgressRing`. In code:
`Wpf.Ui.Controls.MessageBox` and `Wpf.Ui.Controls.InfoBarSeverity`.

**`ui:ControlsDictionary` restyles the plain WPF controls too**, so a bare `ComboBox`, `CheckBox`,
`RadioButton`, `DataGrid`, `ProgressBar`, `ListBox`, `ScrollViewer`, `WrapPanel`, or `TextBlock` is
already Fluent and is **not** a hand-roll. Do not flag one.

Three specific, verified conventions:

- **A persisted boolean setting in the settings window is a `ui:ToggleSwitch`.**
  `src/Scribe.App/Settings/SettingsWindow.xaml` contains 11 `ui:ToggleSwitch` elements and **zero**
  `<CheckBox` elements. A `CheckBox` is used for a per-row or in-form selection instead, in
  `QuickAddWindow.xaml:172` ("Match whole words only") and in the `CleanupRowTemplate` of
  `DictionaryCleanupWindow.xaml`. A new settings toggle added as a `CheckBox` is 🟡: it is the one
  place the settings page would stop looking like Windows Settings.
- **A dialog is `Wpf.Ui.Controls.MessageBox`, not `System.Windows.MessageBox`.** The reasoning is
  written down at `src/Scribe.App/Settings/SettingsWindow.xaml.cs:3840-3844`, on `ShowThemedMessage`:
  a Fluent-themed dialog "replacing the dated Win32 `System.Windows.MessageBox`". `ConfirmAsync`
  (`:3827-3838`) is the two-button sibling, and `App.ShowSingleInstanceNotice`
  (`src/Scribe.App/App.xaml.cs:522-538`) proves one works with no `Owner` at all, driven by a nested
  `DispatcherFrame`. A **new** `System.Windows.MessageBox.Show` in `Scribe.App` is 🟡.
- **A non-blocking notice is the shared `ui:InfoBar`, not a new modal.** `InfoNotice`
  (`SettingsWindow.xaml:222`) is raised through `ShowInfo(message, InfoBarSeverity)`
  (`SettingsWindow.xaml.cs:3863-3878`), which sets severity, opens the bar, and auto-dismisses on a
  6 second `DispatcherTimer`. There are more than fifteen callsites. A new modal dialog for a success
  or summary message is 🟡; point at `ShowInfo`.

**🟡 Important, hard flag:** the diff builds a control out of `Border`, `Grid`, and `TextBlock` (or a
new `UserControl`) that duplicates a WPF-UI control already used in this repository. Name the
`ui:` type and a live callsite. **Confirm the type exists in WPF-UI 4.3.0 before naming it**; a
finding that tells the author to use a control the package does not ship is worse than no finding.

**Not a hand-roll: retemplating a standard control.** That is the established shape here, and it is
what you should recommend when a stock control is nearly right. Two live examples:

| Style key | File | What it retemplates |
| --- | --- | --- |
| `WordChip` | `QuickAddWindow.xaml:23-67` | `ToggleButton`, with a header comment saying why a plain button was rejected |
| `OverlayZone` | `SettingsWindow.xaml:102-135` | `RadioButton`, one cell of the 9-anchor position picker |

**Also not a finding: the settings nav rail is a `ListBox`, not `ui:NavigationView`.**
`SettingsWindow.xaml:123-131` uses a `ListBox` named `NavList` whose `ItemContainerStyle` is
`BasedOn="{StaticResource {x:Type ListBoxItem}}"`, so it inherits the Fluent container style. This is
a deliberate, working divergence. Do not propose a `NavigationView` migration.

---

## §2. The theme follows Windows, and every theme reaction is failure tolerant

**This is the section with an incident behind it.** AGENTS.md, under "A green build proves very little
here", lists three defects that shipped warning clean in one release, and one of them is *a theme
watcher that threw and silently forced the wrong theme*. The current code is shaped around that, and a
change that removes the shaping is the finding.

The startup path, all in `src/Scribe.App/App.xaml.cs`:

- `InitializeApplicationTheme` (`:1131-1143`) applies the current theme once, then subscribes to
  `SystemEvents.UserPreferenceChanged` **inside its own try/catch**, logging a warning if the
  subscription fails. A failed subscription costs live theme switching, not startup.
- `OnUserPreferenceChanged` (`:1145-1160`) filters to `UserPreferenceCategory.General`, then marshals
  through `Dispatcher.BeginInvoke` inside a try/catch.
- `ApplyCurrentWindowsTheme` (`:1162-1181`) wraps `ApplicationThemeManager.Apply(theme, updateAccent: true)`
  in a try/catch whose comment is the rule itself: *"Unable to apply the Windows theme; keeping the
  current app resources."* It fails to a no-op, never to a forced theme.
- `ReadWindowsAppTheme` (`:1183-1217`) reads `AppsUseLightTheme` from
  `HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize` in a try/catch, falls back to
  `ApplicationThemeManager.GetSystemTheme()` in a second try/catch, and only then returns Dark.
- `DisposeThemeWatcher` (`:1219-1222`) unsubscribes inside a try/catch.

The tray path is the same shape. `TrayIconHost` subscribes to `ApplicationThemeManager.Changed`
(`src/Scribe.App/Tray/TrayIconHost.cs:83`), unsubscribes on dispose (`:310`), and `ApplyMenuTheme`
(`:177-187`) wraps `ApplicationThemeManager.Apply(_menu)` in a bare catch whose comment states the
contract: *"The tray menu is a fallback path; a theme refresh failure must not break right-click
access."*

Per-window, the convention is one line, **before** `InitializeComponent()`:
`Wpf.Ui.Appearance.SystemThemeWatcher.Watch(this);`. Live at `SettingsWindow.xaml.cs:158` (comment:
"Match the system light/dark theme + accent colour and enable the Mica backdrop"),
`DictionaryCleanupWindow.xaml.cs:26`, `QuickAddWindow.xaml.cs:91`, and `WelcomeWindow.xaml.cs:25`
(comment: "Match the settings/history windows: follow the OS light/dark theme live").

**🟡 Important, hard flag:**

- A new theme reaction, `ApplicationThemeManager.Apply`, an `ApplicationThemeManager.Changed`
  handler, a `SystemEvents.UserPreferenceChanged` handler, or a `SystemThemeWatcher` call, where a
  throw is not contained. State the symptom: this is the shape that previously forced the wrong theme
  silently.
- A theme catch that falls back to a **hardcoded** theme instead of leaving the current resources
  alone. `ApplyCurrentWindowsTheme` deliberately keeps what is already applied.
- `ThemesDictionary Theme="..."` in `App.xaml` being treated as the answer. It is the design-time
  seed only; `ApplyCurrentWindowsTheme` overwrites it during `OnStartup`. A finding that reads the
  `Theme="Dark"` literal as "Scribe is dark only" is wrong; check `App.xaml.cs` first.
- A new `ui:FluentWindow` with no `SystemThemeWatcher.Watch(this)`, or one that calls it after
  `InitializeComponent()` when every sibling calls it before.
- A hardcoded colour on a themed chrome surface where a `DynamicResource` brush exists.
  `SettingsWindow.xaml` carries **52** `DynamicResource` references and exactly **one** hardcoded
  colour (`Fill="#E5484D"` at `:114`, the recording dot inside the position-picker preview, which is
  quoting the pill's own red on purpose). `QuickAddWindow.xaml` has 14, `WelcomeWindow.xaml` 8,
  `DictionaryCleanupWindow.xaml` 6. The brush names in use are the Fluent
  tokens: `TextFillColorPrimaryBrush`, `TextFillColorSecondaryBrush`, `AccentTextFillColorPrimaryBrush`,
  `ControlFillColorDefaultBrush`, `ControlFillColorSecondaryBrush`, `ControlFillColorTertiaryBrush`,
  `ControlStrokeColorDefaultBrush`, `AccentFillColorDefaultBrush`, `AccentFillColorSecondaryBrush`,
  `TextOnAccentFillColorPrimaryBrush`, `ApplicationBackgroundBrush`.

**Do not apply the last bullet to the pill.** See §6.

**Contrast.** PRODUCT.md's Accessibility section sets the bar: *"Target WCAG AA contrast while
following Windows system theme and accessibility settings."* You cannot compute a contrast ratio
against a Fluent token from reading a diff, and you should not try. What you **can** flag is the
mechanism that breaks it: a fixed foreground hex over a `DynamicResource` background (or the reverse),
which is guaranteed to be wrong in one of the two themes. A pair of hardcoded values that are both
hardcoded is a colour-scheme decision, not a contrast defect, and belongs in Questions with the actual
numbers if you want to raise it at all.

---

## §3. Accessibility: keyboard, focus, and meaning that survives without colour

PRODUCT.md is the requirement, verbatim: *"Preserve keyboard navigation, visible focus, readable
scaling, color-independent meaning, and reduced-motion behavior."* That is the rubric. Here is what
the repository actually does against it, so you flag divergence rather than absence.

**Keyboard navigation.** Quick add is the worked example and the one to cite. Dialog buttons use the
WPF idioms, `IsCancel="True"` and `IsDefault="True"` on the Cancel and Save buttons
(`QuickAddWindow.xaml:190-193`), and `SaveButton.IsDefault` is re-evaluated as the plan changes so
Enter stays on the common path and a deletion has to be a deliberate click
(`QuickAddWindow.xaml.cs:359`, `:373`). The window focuses its first input on `Loaded` rather than a
button (`QuickAddWindow.xaml.cs:127`).

**Visible focus.** No surviving window retemplates a control with a focus visual of its own, so there
is no in-repo precedent to cite here. Review against the rule rather than against an example: a
retemplated control must leave keyboard focus visible, either by keeping the inherited
`FocusVisualStyle` or by giving `IsKeyboardFocused` a trigger of its own.

**🟡 Important, hard flag:**

- A retemplated control that sets `FocusVisualStyle="{x:Null}"`, or that removes an existing
  `IsKeyboardFocused` / `IsFocused` trigger without replacing the visual. The user can still tab to it
  and can no longer see where they are.
- A new interactive control that is mouse-only: a `MouseLeftButtonDown` or `PreviewMouseDown` handler
  on a `Border`, `Grid`, or `Image` with no keyboard equivalent and no focusable control behind it.
- A new window that traps the user: `Escape` does not close it and there is no Cancel button carrying
  `IsCancel="True"`.
- A new state whose **only** distinguishing signal is colour. The house shape pairs colour with text
  or with a glyph. `ShowInfo` sets `InfoBarSeverity`, which carries an icon, **and** always passes a
  message string. The usage trend chart draws accent-coloured bars **and** carries a per-bar `ToolTip`
  **and** repeats the same numbers in the `UsageTrendGrid` DataGrid directly beneath it
  (`SettingsWindow.xaml:1605-1646`). The tray icon changes artwork **and** `ToolTipText`
  (`TrayIconHost.SetState`).

**Accessible names: do not over-flag.** `AutomationProperties.Name` appears on exactly nine controls
across all six App XAML files: five in `SettingsWindow.xaml` (`:386`, `:426`, `:758`, `:843`, `:850`)
and four in `QuickAddWindow.xaml` (`:94`, `:130`, `:157`, `:169`). It is applied where the visible
label sits in a **separate** `TextBlock` that the automation tree would not associate, not
universally. Most icon-only buttons carry a `ToolTip` instead, for example the row-delete
`ui:Button Icon="{ui:SymbolIcon Delete24}" ToolTip="Remove this entry"` at `SettingsWindow.xaml:1086-1088`
and the nine position-picker cells at `:615-632`.

So: **the absence of `AutomationProperties.Name` on a control that has a visible text label is not a
finding.** Flag only a new control that has **no** accessible text at all: no `Content`, no adjacent
label, no `ToolTip`, and no `AutomationProperties.Name`. That is 🟡. Anything softer is a Question.

**Readable scaling.** A fixed `Height` or `Width` on a container of user text, or a `TextBlock`
without `TextWrapping="Wrap"` where the sibling styles set it (`PageSubtitle`, `SettingHint`,
`SettingTitle`, `TipBody` all do), is a 💡 at most. `WordChip` shows the shape when a cap is genuinely
needed: `MaxWidth="320"` plus `TextTrimming="CharacterEllipsis"`, with a comment recording the failure
it prevents (`QuickAddWindow.xaml:28-31`, `:44-47`).

---

## §4. Motion: gate it on the Windows setting, and animate transforms not layout

Two separate rules. Both are written into the codebase already.

### 4a. Every WPF animation checks `SystemParameters.ClientAreaAnimation`

This is the Windows "Show animations in Windows" setting, and it is PRODUCT.md's reduced-motion
requirement in practice.

**There is no WPF animation left in `src/Scribe.App/**` today.** The reference implementation was the
text action dock, which gated six entry points on `SystemParameters.ClientAreaAnimation`, and it was
deleted along with that feature. The rule is unchanged; the precedent is in git history
(`git show 14d39d8 -- src/Scribe.App/TextActions/TextActionDockWindow.xaml.cs`) if you want to read
the shape before recommending it.

Two distinct endings are correct. An animation that **carries information** (a colour change, a press
scale) sets the final value directly when animations are off. An animation that is **decoration only**
returns. Which one applies depends on whether skipping the animation would leave the UI in a stale
state.

**🟡 Important, hard flag:** a new WPF animation, `Storyboard`, `BeginAnimation`, or `DoubleAnimation`
in `src/Scribe.App/**` with no `SystemParameters.ClientAreaAnimation` check, when it is
long-running (`RepeatBehavior="Forever"`), an entrance, or a celebration. Say which of the two
endings applies.

**💡 Suggestion, and prefer silence:** an ungated short one-shot hover or press transition declared
inside a XAML `ControlTemplate.Triggers` block, where the trigger's own exit action restores the
resting value. Do not spend the findings cap here.

**Overlay animations are out of scope for this rule.** `SystemParameters` is WPF and does not exist in
WinUI. `src/Scribe.Overlay/OverlayWindow.xaml:9-30` declares `PulseStoryboard` and
`ProcessingStoryboard`, both `RepeatBehavior="Forever"`, started and stopped by `StartStoryboard` and
`StopStoryboard` (`OverlayWindow.xaml.cs:407-426`), and **neither is gated**. That is the existing
state. If the diff adds a new forever-running overlay animation, raise a **Question**, not a finding:
ask whether a reduced-motion gate belongs there, noting the WinUI-side equivalent would be
`Windows.UI.ViewManagement.UISettings.AnimationsEnabled`, and noting that the pill is only on screen
while the user is actively dictating. Do not assert the API works unpackaged; ask.

### 4b. Anything that animates for a long time animates a `RenderTransform` or `Opacity`

A `RenderTransform` and an `Opacity` both compose on the render thread without invalidating layout,
which is what keeps a surface that animates all day from costing a layout pass per frame.

The overlay pill follows it: `PulseStoryboard` animates `RecDot.Opacity`,
and `ProcessingStoryboard` animates `Dot1Transform.Y`, `Dot2Transform.Y`, `Dot3Transform.Y`, which are
`TranslateTransform` instances hung off each `Ellipse.RenderTransform`
(`OverlayWindow.xaml:9-30`, `:115-123`). Even the settings usage chart follows it: the bars are a
fixed `Height="64"` scaled by `<ScaleTransform ScaleY="{Binding RelativeHeight}"/>` with
`RenderTransformOrigin="0.5,1"` (`SettingsWindow.xaml:1621-1631`).

**🟡 Important, hard flag:** a new `RepeatBehavior="Forever"` or multi-second animation whose target
property is `Width`, `Height`, `Margin`, `Padding`, `Left`, `Top`, `MaxWidth`, `MaxHeight`, or a
`GridLength`. Name the transform that should carry it instead: `ScaleTransform` for a size,
`TranslateTransform` for a move, `Opacity` for a fade.

**Do not flag a short one-shot animation on a layout property**, where the trigger's exit action
restores the resting value. A 120 ms hover transition on a `Height` can be the considered choice, for
instance when animating height rather than width keeps neighbouring text from shifting. That is a
trade, and it is not what this rule is about.

---

## §5. The brand rejects gamification; delight comes from real data

PRODUCT.md, Anti-references, verbatim: *"Avoid flashy consumer gamification, streak pressure,
confetti, arbitrary scores, decorative SaaS dashboards, marketing-style cards, and dense enterprise
telemetry that requires specialist knowledge. Do not sacrifice Windows familiarity for novelty."*
And Design Principles: *"Reward real progress with personal records and honest local data, never
invented scores."*

This is not a style preference in this repository. The register the brand does accept is a brief
transform, a bounce or a pulse, rather than a particle system: it is the same beat of delight without
the consumer-game vocabulary PRODUCT.md rules out, and it costs two transforms instead of a particle
system running on a surface that sits on screen all day.

**🟡 Important, hard flag:** the diff adds a particle or confetti effect, a streak counter, a daily or
weekly goal with a nag, a level, an XP bar, a composite "score" the user cannot trace back to a
measurement, or a badge awarded for engagement rather than for a fact about their data. Quote the
anti-reference line.

**🟡 Important, hard flag:** a decorative dashboard tile. The bar for a metric surface here is that
every number is traceable to local data and is also legible as text. The two live surfaces both clear
it: the Diagnostics panel reports P50 and P95 decode latency and RTF computed from local history, and
the usage trend chart is paired one-to-one with `UsageTrendGrid`, a DataGrid of `Period`,
`Dictations`, `Words` (`SettingsWindow.xaml:1636-1646`). A new chart with no numeric readout beside
it, or a tile whose value is a derived index rather than a measurement, is the finding.

**Do not flag personality itself.** PRODUCT.md's Brand Personality asks for it: *"quietly
delightful"*, with *"moments of earned personality that make repeated use feel satisfying without
turning work into a game."* The line is invented reward versus honest reaction. A bounce when an
action **actually succeeded** is on the right side of it.

**Do not re-open settled UI decisions.** AGENTS.md closes these; a lens reopening one is drifting, not
reviewing: a language picker for the transducer model, an in-process WPF transparent pill, lowering
`SupportedOSPlatformVersion`, or a Windows 10 compatibility story.

---

## §6. Scoping: the floating surface is not a settings window

Getting this wrong is the most likely way this lens produces a confidently wrong finding, so check it
before writing anything about colour or theme.

**The recording pill (`src/Scribe.Overlay/OverlayWindow.xaml`) is fixed dark glass on purpose.** It
carries zero `DynamicResource` references and every colour is a literal ARGB hex: the card gradient
`#802B2F38` to `#73191C22`, the record dot `#E5484D`, the meter gradient, the processing dots
`#8AB4F8`. There is no `RequestedTheme`, no `ElementTheme`, and no theme handler anywhere in
`src/Scribe.Overlay/`. It floats over arbitrary application content, not over Scribe chrome. **Never
flag its hardcoded colours as a missing theme token, and never propose moving anything in it to
`Scribe.Core`**: `Scribe.Overlay.csproj` deliberately has no `ProjectReference`.

**`AllowsTransparency` stays false everywhere.** The .NET 10 WPF `AllowsTransparency` plus
layered-window path intermittently painted an opaque black box, which is the bug that moved the pill
out of process; `src/Scribe.Overlay/TransparentBackdrop.cs:16` records it. A WPF window that wants
rounded corners takes them from DWM instead, via `DWMWA_WINDOW_CORNER_PREFERENCE`. AGENTS.md lists
reintroducing a WPF transparent or layered pill under **Never**.

**🟡 Important, hard flag:** any `AllowsTransparency="True"` added to a WPF window in
`src/Scribe.App/**`. Cross-reference `overlay-process-contract`, which owns this at 🔴 and will carry
the severity; your job is to notice it on a window that lens's trigger might not match.

**Other exceptions in this family, all pre-existing:**

- The two `System.Windows.MessageBox.Show` calls at `src/Scribe.App/App.xaml.cs:436` and `:454` are
  the fatal and fallback data-folder notices, on the path where the data folder itself failed. They
  predate the diff.

---

## Confidence bar

**Hard flag (a Finding)** only when all four hold:

1. The diff **adds or edits** the markup or the handler. Pre-existing UI you noticed while reading
   surrounding context is not this change's finding.
2. You can name the specific rule from §1 to §5 and the file where this repository already follows it.
3. You can state the user-visible symptom in one sentence with no hedge: *"a user who turned
   animations off in Windows still gets a tile that breathes forever in the corner of their screen."*
4. It is a UI correctness or consistency issue, not a taste preference.

The severity ladder for this lens, capped at 🟡:

- 🟡 **Important** for a control nobody can reach with the keyboard, a focus visual removed, a state
  distinguishable only by colour, a theme reaction that can throw, a forced theme on a catch path, an
  ungated forever-animation, a layout property animated forever, a hand-rolled parallel of a WPF-UI
  control in use here, a settings toggle that is not a `ui:ToggleSwitch`, a new `System.Windows.MessageBox`,
  a gamification pattern PRODUCT.md names, or `AllowsTransparency` reappearing.
- 💡 **Suggestion** for a consistency drift with no failure mode: a missing `TextWrapping` on a label
  whose siblings wrap, an ungated 120 ms hover transition, a `Margin` that does not match the sibling
  card. At most one per review, and drop them entirely on a re-review.

**Raise a Question** instead when:

- The surface is new and has no sibling, so there is no established convention to measure it against.
- You suspect a contrast problem but only have hex values and token names, not measured ratios.
- The change adds a WinUI animation to the overlay and you cannot establish whether a reduced-motion
  gate is reachable there.
- The control looks hand-rolled but you could not confirm WPF-UI 4.3.0 ships an equivalent.
- The window is deliberately fixed-dark per §6 and you are unsure whether a new surface joins that
  family or the themed one. Ask which it is.

**Never write** "this will fail the build", "the XAML will not parse", or "the tests will catch this".
The test project references only `Scribe.Core` and cannot see any of this, and AGENTS.md records three
defects in one release that compiled warning clean. The claim carries no weight in either direction.

---

## Output format

The two findings below are **illustrative shapes**, not live defects. `HistoryWindow.xaml` and
`StreakCard` are invented and do not exist. Never cite either as an existing exemplar.

```markdown
## UI shell quality findings

🟡 **New history window skips the system theme watcher and hardcodes its text colour** (`src/Scribe.App/History/HistoryWindow.xaml.cs:31`, `HistoryWindow.xaml:22`)

`HistoryWindow` is a `ui:FluentWindow` but its constructor calls `InitializeComponent()` with no
`Wpf.Ui.Appearance.SystemThemeWatcher.Watch(this)` line, and the transcript `TextBlock` sets
`Foreground="#E6E6E6"` rather than `{DynamicResource TextFillColorPrimaryBrush}`. On a machine set to
light mode the window renders Mica-light with near-white text on it, which is unreadable, and it never
follows a live theme switch. All four sibling windows do this the same way, one line before
`InitializeComponent()`: `SettingsWindow.xaml.cs:158`, `DictionaryCleanupWindow.xaml.cs:26`,
`QuickAddWindow.xaml.cs:91`, `WelcomeWindow.xaml.cs:25`. `SettingsWindow.xaml` carries 47
`DynamicResource` brush references and exactly one hardcoded colour, and that one is quoting the pill's
recording red on purpose.

Fix: add the `Watch(this)` call before `InitializeComponent()` and swap the literal for
`{DynamicResource TextFillColorPrimaryBrush}`.

🟡 **The new streak card animates `Height` forever and is not gated on the Windows animation setting** (`src/Scribe.App/Settings/SettingsWindow.xaml.cs:4912-4930`)

`StartStreakPulse` begins a `DoubleAnimation` on `StreakCard.HeightProperty` with
`RepeatBehavior.Forever`, with no `SystemParameters.ClientAreaAnimation` check. Two problems, both in
PRODUCT.md's Accessibility line, which requires reduced-motion behavior. A user who turned animations
off in Windows gets a card that pulses on the settings page for as long as it is open, and because the
target is a layout property every frame invalidates layout for the whole panel rather than composing on
the render thread.

Fix: open the method with `if (!SystemParameters.ClientAreaAnimation) return;`, and animate a
`ScaleTransform` on the card's `RenderTransform` with `RenderTransformOrigin` set, the way the usage
trend bars already do (`SettingsWindow.xaml:1621-1631`).
```

**If clean:** "UI shell quality clean: the change stayed in the WPF-UI Fluent vocabulary, followed the
system theme through the existing failure-tolerant path, kept every new control reachable by keyboard
with a visible focus state and meaning that survives without colour, gated any motion on
`SystemParameters.ClientAreaAnimation` and animated transforms rather than layout, and added nothing
PRODUCT.md's anti-references rule out."

---

## Exceptions

Do not raise any of these. Each is a shape this repository has on purpose.

- **Plain WPF controls.** `ui:ControlsDictionary` in `App.xaml` already gives `ComboBox`, `CheckBox`,
  `RadioButton`, `DataGrid`, `ProgressBar`, `ListBox`, `WrapPanel`, `ScrollViewer`, and `TextBlock`
  their Fluent look. Using one is not a hand-roll.
- **Retemplating a stock control.** `WordChip` and `OverlayZone` are the established shape for a
  control WPF-UI does not ship, and each carries a comment saying why.
- **The settings nav rail as a `ListBox`.** Deliberate. Do not propose `ui:NavigationView`.
- **The pill's hardcoded colours and missing theme handling.** It is a fixed-dark floating surface
  over arbitrary content, by design. See §6.
- **`ThemesDictionary Theme="Dark"` in `App.xaml`.** A design-time seed that `ApplyCurrentWindowsTheme`
  overwrites at startup, not a decision to ship a dark-only app.
- **`AutomationProperties.Name` missing from a control that already has a visible text label.** Nine
  controls in the whole App carry it, applied where the label is detached. Absence is the norm.
- **A short one-shot hover, press, or entrance animation that is ungated**, including one on a layout
  property, where the trigger's exit action restores the resting value.
- **`WordChip` being `Focusable="False"` and `IsTabStop="False"`** (`QuickAddWindow.xaml:131-132`).
  The chips are a pointer shortcut into two `ui:TextBox` controls that are themselves in the tab
  order, they carry `AutomationProperties.Name="{Binding Text}"`, and `IsChecked` is bound `TwoWay`
  specifically so a toggle arriving from assistive technology writes back to the model. The comment at
  `:121-125` records the bug the `TwoWay` binding fixed.
- **The two `System.Windows.MessageBox.Show` calls in `App.xaml.cs`** (`:427`, `:454`). Pre-existing
  data-folder failure notices.
- **Overlay animations not honouring a reduced-motion setting.** `SystemParameters` is WPF. Existing
  overlay storyboards are ungated; a new one is a Question, not a finding.
- **Anything about the pill's process boundary, the pipe commands, the `OverlayPosition` and
  `OverlayAnchor` enum twins, the Job Object, or `TransparentBackdrop`.** That is
  `overlay-process-contract`'s beat entirely.
- **A decision, a validator, or a builder added to a `.xaml.cs`.** That is `core-app-layering`'s beat.
  Do not duplicate it here.
- **Comment style and the em dash or en dash ban.** `comment-and-dash-hygiene` owns both, including in
  UI strings.
- **A window growing by handler count alone.** More handlers is what a settings page does.
- **Tools under `tools/`.** `Scribe.Evals`, `Scribe.AsrCheck`, `Scribe.Benchmarks`, and
  `Scribe.InjectionLab` are harnesses, not the shipped shell.

---

## Completion marker

End your output with this exact line, on its own, with nothing after it:

```
[[agent-done:ui-shell-quality findings=<n> coverage=complete]]
```

`<n>` is the number of findings you rendered, `0` on a clean pass. Use `coverage=incomplete` and name
the gap in one trailing clause when you could not read `diff.patch`, or when the diff referenced files
you needed for context and could not open. SKILL.md Step 3 counts a lens as "results used" only when
this line is present, so a clean pass still emits it.
