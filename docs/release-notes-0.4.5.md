# Scribe 0.4.5

This release lets a spare mouse button be your push-to-talk key. The middle button and the Back and
Forward side buttons bind directly in Settings, and any other button binds through the key your
mouse's software sends for it, such as F13. Set also records shortcuts such as Ctrl+Shift+F13. If you
use keys only, your hotkeys work as before and Scribe doesn't watch the mouse at all.

## What changes when you update

- **Your hotkeys stay as they are.** Nothing about the mouse changes until you bind a mouse button:
  with keys only, Scribe doesn't watch the mouse.
- **Set records more.** Besides one or two keys, Set now records the middle, Back and Forward mouse
  buttons, and a key or button held with Ctrl, Alt or Shift, including more than one of them, such as
  Ctrl+Shift+F13. Before, it stopped at two keys.

## Mouse buttons

- **Middle, Back and Forward bind directly.** In Settings, General, choose Set and press the button
  with the pointer on the Settings window: the middle button (on most mice, pressing the wheel), or the
  Back or Forward side button. Either hotkey can use one, in hold or toggle mode, on its own or after a
  key, such as Ctrl then Back; for a key and a button together, press the key first. Left and right
  clicks can't be used.
- **Every other mouse button binds through the key it sends.** Windows passes only five mouse buttons
  to apps: left, right, middle, Back and Forward. For any other button, set it to a key such as F13 in
  your mouse's software, then choose Set and press the button. F13 to F24, media and browser keys, and
  shortcuts with Ctrl, Alt or Shift all bind this way, and a button that already sends such a key binds
  as it is. A key such as F13 on its own works best: with a shortcut, Windows still sees its Ctrl, Alt
  and Shift keys, and Set warns you when a Ctrl+Shift or Alt+Shift shortcut could switch your keyboard
  language or layout.
- **A button bound on its own no longer does its usual job in other apps**, so a bound Back button
  stops going back in your browser while Scribe runs. Pressed with Ctrl, Shift, Alt, Win or the
  Narrator key, it still does its usual job, and so does every button while dictation is paused from
  the tray. In a chord such as Ctrl then Back, only the chord is Scribe's: Back on its own still goes
  back.
- **If Windows briefly stops passing input to Scribe** (it does this to any app that answers too
  slowly), a click made or held while that lasts reaches the app under the pointer. Scribe reconnects,
  normally within 30 seconds, and ends a dictation a mouse button hotkey started. If you were holding a
  bound button across that time, letting go of it can do its usual job once, which for Back or Forward
  is one step back or forward. That never leaves a button held down in Windows.
- **Scribe watches the mouse only while one of your hotkeys uses a mouse button**, because while it
  does, Windows passes every pointer move through Scribe. After you remove such a hotkey, Scribe keeps
  watching only if it is still waiting to see that button let go (you were holding it, say), and then
  only until it next sees that button pressed or let go.
- Scribe never sends mouse input of its own: it never presses or releases a mouse button for you.
- If you go back to an earlier version, a mouse button hotkey shows its name there but does nothing;
  choose a key again. A key or shortcut such as F13 or Ctrl+Shift+F13 keeps working there.
- The welcome says a mouse button can be your push-to-talk key, and the hints under the hotkeys in
  Settings say how to bind one.

## Under the hood

- No dependency changes: the speech engine, the AI libraries and the Windows App SDK are the same
  builds as in 0.4.4.
- The hotkey self-healing that releases a key Windows still thinks is held covers keys only. It no
  longer starts a release once you choose Set, until Set is done, and immediately before each release
  it checks that nothing has reset Scribe's view of your keys since the release was asked for. After
  such a reset it waits for your hotkey's next release instead of guessing.
- The log says whether each hotkey uses a key, a mouse button or both, and when Scribe's mouse hook is
  added, removed or found gone. It records no pointer movement and no other clicks.

## Known limitations

- A mouse button bound on its own doesn't do its usual job in other apps while Scribe runs, unless you
  press it with a modifier or pause dictation. Choose another button or a key if that gets in your way.
- A game that reads the mouse directly may still see a bound button.
- Another program's mouse hook may not see a bound button either, so a utility that remaps that same
  button can stop working on it while Scribe runs. Let the utility send a key such as F13 instead, and
  bind that key.
- While Scribe watches the mouse, every pointer move passes through Scribe, so a moment when Scribe is
  busy can briefly hold up the pointer.
- Set records one modifier and a key, such as Ctrl+F13, as those two exact keys, left or right. If a
  mouse button set to such a pair doesn't respond, set it to a key on its own, such as F13, or add a
  second modifier.
- Set ignores a few unassigned or reserved key codes, so a button whose software sends one of those
  can't be bound. F13 to F24 and the media and browser keys all work.
- Very rarely, after a press of a bound button that Scribe could not see (on a Windows security
  prompt, say), letting go of it while a window of an app running as administrator is in front can
  leave Windows thinking the button is still held. The next time you use the button with another
  window in front, Windows sees it let go, which for Back or Forward can navigate once.
- A key you press at the very moment the self-healing releases it can be let go in Windows while you
  hold it; let go of the key and press it again. If another program holds up keyboard input for more
  than a quarter of a second, a release the self-healing is already sending can also arrive just after
  you choose Set.
- Like other apps that are not running as administrator, Scribe's hotkeys, mouse buttons included, may
  not respond while a window of an app running as administrator is active.

## For the macOS app

Mouse button hotkeys are Windows only for now. The native Apple Silicon port proposed in issue #40 has
shipped since 0.3.16; build the app from macos/README.md (thanks to x3nc0n). A performance and privacy
overhaul of the port is in review (#81).

Available for Windows x64 and Arm64. Microsoft Store submission is handled automatically; Store
availability follows certification.
