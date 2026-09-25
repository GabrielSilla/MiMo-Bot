# Brobot.Sender Internals — Overview, Tabs, Conexão, Tema

This is the app for whoever actually assembled a Brobot — not a dev tool, and
user-facing branded **"Peemo"** throughout (window titles, tray tooltip, every
string an end user sees) even though the code/project/namespace keep the
Brobot name everywhere. It runs in the system tray and, when opened, shows
three tabs — **no separate settings window**: there used to be one
(`SettingsWindow`, COM-port picker + TCP host/port + theme combo), deleted
entirely once Serial was dropped from this app (see [connection.md](connection.md))
— the Conexão card (IP:port field + Conectar/Desconectar)
is now just another card in the checklist tab, and the theme combo already
had its own duplicate card there too. Checking a box turns on a background
watcher that decides *when* to send something; it never decides *how it
should look* — that's still 100% Core's call per [overview.md](overview.md)'s
"The one rule that matters".

**Peemo wordmark, then a shared subtitle, then three tabs — Configurações
Gerais, Mini Games, Conquistas** (`PeemoTabControlStyle`/`PeemoTabItemStyle` in
`MainWindow.xaml`'s `Window.Resources`) — replaced what used to be a plain-
`Visibility` page swap between the checklist and an Anti-Stress button's game
picker: with a third, permanent destination (Conquistas, deliberately empty
for now — nothing here yet) alongside the checklist, a real selector was the
right shape rather than something to keep avoiding. The wordmark and the
subtitle both sit in the outer `Grid` *above* the `TabControl`, not inside any
one `TabItem`'s own content — the logo is the app's own identity, shared by
all three tabs, and a single `TabSubtitleText` swaps its own text to match
whichever tab is selected rather than each tab carrying a duplicate copy.
`PeemoTabControlStyle` retemplates `TabControl` down to a `TabPanel` header row
(`HorizontalAlignment="Center"`, so the three pills sit centered under the
subtitle rather than pinned to the left) over a plain `ContentPresenter` — the
stock template draws a bordered content box that doesn't match this window's
flat-card look at all, same reasoning every other retemplated control here
already has. `PeemoTabItemStyle` is a flat pill header (`InkBrush` fill when
selected, `HighlightBrush` otherwise) — its selected/unselected foreground
swap has to reach through `ContentPresenter`'s own auto-generated `TextBlock`
via the `TextElement.Foreground` attached property on a named
`ContentPresenter` rather than a direct `Setter` on `TabItem.Foreground`,
since a plain string `Header` doesn't inherit that the way a normal panel's
children would.
`MainTabControl_SelectionChanged` is what keeps `TabSubtitleText` in sync,
and it has to guard against a real gotcha: `SelectionChanged` is a bubbling
routed event that `TabControl` inherits from the same `Selector` base
`ComboBox` does, so a selection change on any `ComboBox` nested inside a
tab's own content (`TemaComboBox`, `PensamentosIaComboBox`, ...) bubbles all
the way up and would fire this handler too — guarded by comparing
`e.OriginalSource` against `MainTabControl` itself, not just checking
`sender` (which is always `MainTabControl`, the object the handler is
attached to, regardless of which nested control actually raised the event).

The **Trabalho** tab (second, right after Configurações Gerais) holds the
work-day cards — Pausa, Atividade da IA, Ferramentas de Dev, Relatório —
moved out of Configurações Gerais so that tab stays about Peemo itself. The
controls kept their names, so handlers and `SaveButton_Click` are unchanged;
Trabalho has its own `TrabalhoSaveButton` wired to the same handler (it saves
everything, and flashes "Salvo!" on whichever button was clicked), since
Pausa's and Relatório's times only persist on save. Tab order is Configurações
Gerais, Trabalho, Mini Games, Conquistas, Agenda — `MainTabControl_SelectionChanged`
maps subtitles by index, so reordering tabs means updating that switch.

The **Configurações Gerais** tab (six feature cards — Conexão, Hora, Clima,
Atividade da IA, Mídia, Jogos — a Tema card, an info card, a "Salvar
configurações" button, warm cream/tan palette) follows a supplied design
reference closely — see `MainWindow.xaml`'s `Window.Resources` for the
color brushes and the custom `CheckBox`/`ComboBox`/`Button`/`TextBox`
control templates (WPF's stock chrome doesn't look anything like flat
rounded cards, so all four are fully retemplated rather than just
re-colored). The checkmark `Path` inside `PeemoCheckBoxStyle` needs explicit `HorizontalAlignment`/
`VerticalAlignment="Center"` — its `Data` uses absolute coordinates for a
small checkmark shape, and a `Path` with `Stretch="None"` (the default)
draws that geometry anchored to its layout slot's top-left corner, not
centered in it, so without those two setters the check sits visibly
off-center in the 28x28 box — this was a real bug, fixed once.


- **Conexão card**: a single `TextBox` (`ConnectionAddressTextBox`, "IP:porta"
  in one field, e.g. `192.168.1.50:5555` — parsed by splitting on the *last*
  `:` so a literal IPv6 address wouldn't break it) plus a Conectar/Desconectar
  `Button`. `UpdateConnectionStatus()` (a 200ms `DispatcherTimer` tick) is the
  **single source of truth** for both the status text and the button's own
  label/enabled-state — it used to only touch the status text, while the
  button's content was set optimistically by whichever code path last called
  Connect/Disconnect and never corrected afterward; if the TCP connection
  dropped on its own (Core unreachable, network blip), the button stayed
  stuck reading "Desconectar" while `IsConnected` was already false, so
  clicking it hit the *reconnect* branch instead of actually disconnecting
  anything — read as "the button doesn't work". This was a real bug, fixed
  once. The address is a **readout, not a field** — a `TextBlock`, not a
  `TextBox`: Peemo's IP is discovered, never typed, and the app's own
  `_coreHost`/`_corePort` are the source of truth the card displays. It used
  to be the other way round, with the text in an editable box *being* the
  truth, which is precisely what broke every time DHCP moved Peemo — the
  address was only ever as correct as whatever someone last typed. (With the
  field gone, `TryParseAddress` and its "Endereço inválido" message went with
  it; there's no longer any input to be invalid.)
  `RestoreSettings()` loads the saved host:port for display, but
  **deliberately does not connect to it** — every launch starts with a fresh
  sweep (`StartNetworkSweep(trustPreviousAddress: false)`) instead. Between
  one run and the next the app may have been closed for days: long enough for
  Peemo to have been given a different address, and for DHCP to have handed its
  old one to some other device — where `PeemoDiscovery`'s
  "unconfirmed-but-listening at the address already in use" fallback would
  adopt a stranger. Asking the network beats trusting a note from last time,
  and it costs one ~3.2s sweep at startup. That's the entire difference
  between the two `trustPreviousAddress` cases: a mid-session recovery *does*
  trust the address, because it was demonstrably reaching Peemo moments ago.
  Because a saved address goes stale on its own whenever DHCP moves Peemo,
  `UpdateConnectionStatus` also drives an automatic network sweep
  (`TryStartNetworkSweep` → `PeemoDiscovery`, see [connection.md](connection.md)):
  once `ConnectTcp` has been getting nowhere for `SweepAfterFailingFor` (8s),
  the address itself is treated as the suspect and the network is searched for
  Peemo; whatever is found replaces the field's text, reconnects, and is written
  **straight to the settings file without waiting for "Salvar configurações"**
  (`PersistDiscoveredAddress`) — unlike every other setting there, Peemo's
  address isn't a preference someone chose, it's a fact about where the device
  currently is, and not saving it would mean re-sweeping on every launch.
  The 8s delay is what keeps an ordinary blip (Peemo still booting, WiFi
  reassociating) from triggering a sweep that `ConnectTcp`'s own 500ms retry
  loop was about to make unnecessary, and `SweepCooldown` (30s) is what keeps
  Peemo simply being *switched off* — indistinguishable from Peemo having moved,
  without sweeping — from sweeping back-to-back forever. This is polled rather
  than event-driven because `BrobotConnection` has no "gave up" event to
  subscribe to: `ConnectTcp` retries forever by design, so "how long has this
  been failing" is something only the status poll can notice.
  The button's label tracks **connected and nothing else**: "Desconectar" only
  when there is a live connection, "Conectar" in every other state. It used to
  read "Desconectar" while merely connecting or sweeping as well, on the
  reasoning that a click could call those off — but `ConnectTcp` retries
  forever, so a search that found nothing left the button reading "Desconectar"
  indefinitely with nothing connected to disconnect from. This was a real bug,
  fixed once. It's disabled only while a sweep is actually in flight, the one
  state where a second click would stack a duplicate search.
  Clicking "Conectar" means **try now**, and what that entails depends on the
  state: with a known address and nothing currently trying it (the
  reconnect-after-Desconectar path) it's one direct `ConnectTcp`; with nothing
  known, or with a known address already being retried fruitlessly, it's an
  immediate sweep that skips the delay/cooldown `TryStartNetworkSweep` would
  impose. That sweep is deliberately untrusting — if the address being retried
  were going to work, the retry loop would already have connected. With the
  field gone, this is also the only way a first-run install ever gets an
  address at all.
  The address readout only ever shows an address that means something at that
  moment: the one being probed while a sweep runs, the one that actually
  reached Peemo while connected, and nothing at all otherwise. In particular it
  stays blank while merely *connecting* — printing the address being retried
  next to "Conectando..." reads as though that address were live, which is
  exactly backwards when the usual reason for being stuck there is that the
  address is dead.
  While a sweep runs, the card's address readout shows the address currently
  being probed and the status line shows `Procurando Peemo... 87/253`, both
  repainted by a dedicated `SweepProgressTickInterval` (333ms) `DispatcherTimer`
  rather than by the progress reports themselves. `OnSweepProgress` only
  records where the sweep has got to; the tick is what paints. Repainting per
  report would be ~100 repaints a second — an unreadable blur — and a fixed
  tick also keeps the cadence independent of how the probes happen to bunch
  up (see `ProbeLaunchStagger`, which fixes the bunching itself).
  `ShowTransientStatus` exists because `ConnectionStatusText` is otherwise
  rewritten by the 200ms poll: a one-off message ("Endereço inválido", "Peemo
  não encontrado na rede") set directly would be overwritten on the very next
  tick, before anyone could read it — which was already true of the
  invalid-address message before the sweep existed.


- **Icons** come from the `MahApps.Metro.IconPacks.Material` NuGet package
  (Material Design Icons as `<iconPacks:PackIconMaterial Kind="...">`), not
  hand-drawn `Path` geometry — a first pass tried composing icons (a cloud,
  a brain) from overlapping `EllipseGeometry`/`RectangleGeometry` shapes
  stroked directly, which draws *every* edge where shapes cross, not just the
  outer silhouette, producing an illegible tangle; `CombinedGeometry` with
  `GeometryCombineMode="Union"` would fix that same trick, but a maintained
  icon set was simply more reliable than hand-authoring six icons. An
  implicit (no `x:Key`) `Style` targeting `PackIconMaterial` centers every
  icon in its badge — `Border` doesn't center a fixed-size child by default,
  so without it every icon sits pinned to the badge's top-left corner.
- **`src/peemo-trimmed.png`** is the supplied `peemo.png` wordmark with its
  transparent margin cropped off. The source is a 1254x1254 *square* for a
  wordmark that's actually wide and short (the glyphs occupy roughly
  x:206-1084, y:524-754 of that square) — displayed directly at a normal
  header height, almost all of that height is wasted transparent padding and
  the visible logo shrinks to an illegible sliver. Both files ship as
  `Resource` items in the csproj; only the trimmed one is referenced from XAML.
- **`RootScrollViewer.ScrollToTop()`** is called every time `ShowFromTray()`
  shows the window. WPF's default keyboard-focus-follows-into-view behavior
  auto-scrolls an ancestor `ScrollViewer` to whichever control ends up
  focused when the window becomes visible (e.g. the first checkbox) —
  without forcing it back to the top, the logo/subtitle at the very top of
  the card list would sit permanently scrolled just out of view.

- **Tema** is a `ComboBox` (`TemaComboBox`, `ThemeManager.Available`) picking
  between "Peemo Classic", "Peemo Matrix", "Peemo P2-M2" and "Peemo-84" — one control driving two
  unrelated systems: `ThemeManager.Apply` swaps this app's own WPF skin
  (`ThemeInfo.ResourcePath`), and `TemaComboBox_SelectionChanged` also sends
  Core its own `THEME <CoreTheme>` command (`ThemeInfo.CoreTheme`,
  `DEFAULT`/`MATRIX` — see PROTOCOL.md), which changes how Core itself draws
  the display. They just happen to both be about "appearance", and from the
  user's point of view Peemo Classic/Peemo Matrix reads as one choice, not
  two — this used to be two separate cards (Tema for the WPF skin, a "Tela
  do Peemo" checkbox for Core's `THEME`), folded into this one picker
  instead. "Peemo Matrix", "Peemo P2-M2" and "Peemo-84" all reuse the same
  `PeemoClassic.xaml` resource as "Peemo Classic" — there's no dedicated WPF
  skin for any of them in this app's own UI (yet), only for Core's display,
  so selecting them changes what Core shows without changing how
  Brobot.Sender itself looks. Like Clima's `WEATHER`
  badge, `THEME` is a persistent flag Core forgets across its own reboot, so
  `UpdateConnectionStatus` resends the selected `THEME` on reconnect
  (checking `TemaComboBox`'s currently-selected `ThemeInfo.CoreTheme`) the
  same way it resends the last weather reading — for any non-`DEFAULT`
  value, not just `MATRIX`: that check was literally `== "MATRIX"` until
  P2-M2 was added, which would have silently dropped the new theme on
  every reconnect. `DEFAULT` still needs no resend, since that's already
  Core's own boot default.
  A second `ComboBox` (`ClassicColorComboBox`,
  `ThemeManager.AvailableClassicColors`) sits beside it, sending Core's own
  `CLASSICCOLOR <CoreColor>` (see PROTOCOL.md) — Peemo Classic's selectable
  primary color (eyes, corner icons, weather/clock badge): Azul (original),
  Verde (reuses Peemo Matrix's own green), Âmbar (reuses Peemo-84's own
  amber), Vermelho, Rosa, Branco. Hidden (`Visibility.Collapsed`, not
  disabled) whenever anything but "Peemo Classic" is selected
  (`RefreshClassicColorVisibility`, called from `TemaComboBox_SelectionChanged`)
  since Core ignores `CLASSICCOLOR` entirely on every other theme — a picker
  left visible-but-inert there would imply a choice that does nothing.
  Same persistent-flag treatment as `THEME` in every other respect:
  `SenderSettings.ClassicColor` is saved immediately on change (not gated
  behind "Salvar configurações"), and `UpdateConnectionStatus` resends it on
  reconnect for any non-`Blue` value — regardless of which theme happens to
  be selected at that moment, since Core holds the color independently of
  the active theme and only reads it while rendering `DEFAULT`.

- All three monitors, and `BrobotConnection` itself, raise their events off
  background threads — every handler in `MainWindow` re-enters via
  `Dispatcher.Invoke` before touching UI or calling `SendCommand`.
- The tray icon (`System.Windows.Forms.NotifyIcon` — WPF has no tray control of its
  own, hence `UseWindowsForms` alongside `UseWPF` in the csproj, which pulls
  `System.Windows.Forms`/`System.Drawing` into scope and makes `Application`,
  `MessageBox`, `Brushes`, `Color` etc. ambiguous with their WPF namesakes
  everywhere in this project — spelled out fully, or aliased as `Forms`/`Drawing`/
  `Media`, throughout) is built at runtime (`CreateTrayIcon`) from
  `src/peemo-b.png` (a "Peemo" wordmark on black, ships as a `Resource` item
  same as `peemo-trimmed.png`) via `Application.GetResourceStream` +
  `System.Drawing.Image.FromStream`, scaled down to the small size a tray
  icon actually needs — not a shipped `.ico` asset, and not the flat teal
  square it used to be before that PNG existed. `MainWindow.xaml`'s
  `Window.Icon` points at the same PNG, which covers both the title-bar icon
  and the taskbar icon (WPF uses one `Icon` property for both). `MainWindow` itself starts
  with no window shown at all — `App.xaml` has no `StartupUri`; `App.xaml.cs`
  constructs `MainWindow` directly without calling `Show()`, so there's no
  show-then-hide flash at launch. Closing the window (the X) hides it back to the
  tray instead of exiting — only the tray menu's "Sair" calls
  `Application.Current.Shutdown()`.
