namespace AiUsage.Tests;

/// <summary>
/// Documented gap, same reasoning <see cref="AccessibilityTests"/> already applies to live WPF
/// instantiation in this suite: the three code paths that set <c>ConfirmWindow.DialogResult</c>
/// (confirm click to true, cancel click to false, Esc/close to false) are not covered by an
/// automated test here.
///
/// <c>Window.DialogResult</c>'s setter throws <c>InvalidOperationException</c> unless the window is
/// currently being shown modally via <c>ShowDialog()</c>, so a construct-only instance (no modal
/// loop) can never reach those handlers to prove the return values, even by calling the private
/// click handlers directly through reflection.
///
/// Getting far enough to try that was itself checked empirically, not assumed: an earlier attempt at
/// constructing a real <c>ConfirmWindow</c> outside the actual AI-Usage.exe process found that WPF
/// locks <c>Application.ResourceAssembly</c> to the process's entry assembly the first time it is
/// touched (confirmed unfixable from test code by disassembling WPF's own
/// <c>Application.set_ResourceAssembly</c>: the setter silently no-ops once
/// <c>Assembly.GetEntryAssembly()</c> is non-null, which it always is under vstest), and this
/// window's <c>Icon="pack://application:,,,/Assets/app.ico"</c> - a bare, unqualified pack URI -
/// resolved against exactly that assembly, so it failed from any other host process, throwing a
/// <c>XamlParseException</c> during <c>InitializeComponent()</c> before <c>DialogResult</c> would
/// ever come into play. <see cref="RenderedTextContrastTests"/> later fixed this properly: every
/// window's <c>Icon</c> (and SettingsWindow's own app-mark <c>Image</c> in its About category) now names its
/// assembly explicitly (<c>AI-Usage;component/Assets/app.ico</c>) instead of depending on whichever
/// assembly happens to be the process entry point - the normal, idiomatic WPF form, safe in the
/// shipped app since it is still the exact same embedded resource in the exact same assembly.
/// <c>ConfirmWindow</c> now builds cleanly in this process too. What still cannot be proven here is
/// <c>DialogResult</c> itself: its setter throws unless the window is currently shown modally via
/// <c>ShowDialog()</c>, which needs a real message loop this project deliberately keeps out of
/// automated tests - so those three outcomes still need a manual click-through on the real app.
/// </summary>
public class ConfirmWindowTests;
