# Editor localization

The local UTF-8 catalogs have matching keys in `zh-CN.json`, `en.json` and `ja.json`.
The November 2026 rewrite covers all 1,223 current editor entries in each language,
including diagnostics, camera mouse controls and timeline prefixes. Keep useful short labels;
write instructions and errors in clear, natural language that describes the actual action or
next step. Do not invent controls, causes, automatic searches, persistence or performance
guarantees. Distinguish applying an edit from saving the project file, preserve byte-based
format limits, and match quoted button names to their actual localized labels. Authoring
review must preserve copyright text, placeholder counts, and concatenated spaces/newlines.

`manifest.json` keeps a read-only legacy directory of original source literals to stable keys and
lists the formal AXAML views, including startup, covered by the static migration. Generated keys
derive from SHA-256 of their exact original Chinese text; distinct semantic contexts can use explicit
named IDs. Existing keys remain stable when visible wording is revised. No translation
service, credentials or dependencies are involved.

Call `EditorLocalization.SetLanguage(preferences.Language)` on the UI thread after Avalonia
initialization and before constructing the editor. Static UI text uses
`{DynamicResource Mda.Text.…}`. Avalonia 11.3.22's native `ResourceDictionary.SetItems` updates this
class's merged dictionary with one resource-change notification; theme resources are left intact.

New or revised computed view-model strings use `EditorLocalization.Text("Mda.Text.…")` or
`EditorLocalization.Format("Mda.Text.…", frame)`. Existing exact source literals remain supported
through the frozen `manifest.json` `literalToKey` directory. Translate the format template before inserting
values. Use `EditorLocalization.Observe(this, static vm => vm.RefreshLocalization())` for view
models. The observer keeps only a weak target reference and removes dead targets when registering
or changing language, so closed dialog instances do not require explicit disposal. Refresh
computed text and localized choice collections while preserving selection indices and draft
values. `EditorLocalization.Error` retains UI validation templates and explicit localized field
labels, allowing already displayed validation to change language without translating user names.
Visible Chinese values may repeat across IDs; wording does not determine identity. The runtime
resolves explicit IDs first, then frozen legacy aliases, including when Chinese is selected.
Do not regenerate IDs or legacy aliases when revising visible wording, and do not add an alias
that begins with `Mda.Text.` or points to an absent catalog key. New contextual meanings use an
explicit ID, preserving each ID's existing English/Japanese translation independently.
Before Avalonia initialization, the existing default-Chinese literal path returns its input without
loading assets. The first resource installation raises `Changed` after installing the dictionary,
so previously constructed observing view models refresh even when Chinese remains selected.
Pre-initialization explicit-ID behavior is unchanged; initialize Avalonia before resolving IDs.
Do not translate model, bone, morph, display-group, material, object, file or stable preset names.
The PMM model identity `カメラ・照明` is deliberately excluded. Built-in role and style display labels
can be localized while their IDs and indices remain unchanged. Language choices use their native
names. Brand names, axis labels, icons, file extensions and invariant units remain unchanged.
`Mda.Text.Context.OptionOff` distinguishes an effect/AA Off choice from the Chinese-equivalent
dialog Close action; use that resource key directly for disabled effect options.

`scripts/localize-editor-static.py` is opt-in. Its default mode only reports matches. `--write`
replaces mapped literal attributes in manifest-allowlisted AXAML; `--check` fails for uncovered
Chinese UI literals. It preserves formatting, bindings and all unrelated attributes. It does not
walk runtime controls or overwrite bound names. Repeating the migration is safe.

Read-only source checks cover catalog key sets, frozen source mappings with valid targets, stable
SHA-256 IDs derived from the original literals, nonempty values and matching numbered composite-format
placeholders. Duplicate display values are valid; duplicate JSON properties are not. Formal views use
localized static UI references. Service errors and diagnostics from texture
loading (including TGA/DDS validation), PMM interchange, workspace capacity and motion-name
gates, export and preference tool paths also use Text/Format at their construction sites, so their
exception types and parameter names remain unchanged while their messages follow the selected
language. User-supplied names and paths are inserted only after translating the template.

The catalog also includes the unnamed-model fallback and the combined MDA/MMD project file
filter. The role-selection dialog translates only its instruction, leaving the file name intact.
These source checks establish catalog coverage; integrated English/Japanese rendering and live
language-switch checks remain the application workflow probe’s responsibility.

The implementation uses Avalonia's existing resource API rather than adding a localization
framework. References: [resource documentation](https://docs.avaloniaui.net/docs/app-development/resources),
[pinned 11.3.22 ResourceDictionary source](https://github.com/AvaloniaUI/Avalonia/blob/11.3.22/src/Avalonia.Base/Controls/ResourceDictionary.cs).
The existing Avalonia version is [MIT licensed](https://github.com/AvaloniaUI/Avalonia/blob/11.3.22/licence.md).
No third-party implementation was copied.
