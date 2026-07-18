# EMS Scout Design System

## 1. Atmosphere & Identity

EMS Scout is a calm, high-density operations workbench for facilities staff. It should feel dependable, direct, and native to Windows rather than decorative or promotional. The signature is the list-detail workbench: a stable navigation shell, a compact selectable list, and a clearly sectioned task surface that keeps operational context visible while the user acts. Design read: an existing Microsoft desktop operations tool for Chinese-speaking facility operators, preserving the current Fluent WinUI language. `DESIGN_VARIANCE: 3`, `MOTION_INTENSITY: 2`, `VISUAL_DENSITY: 7`.

## 2. Color

### Palette

| Role | WinUI resource | Usage |
|---|---|---|
| Page surface | `ApplicationPageBackgroundThemeBrush` | Application and page background |
| Card surface | `CardBackgroundFillColorDefaultBrush` | Workbench cards and panels |
| Card border | `CardStrokeColorDefaultBrush` | One-pixel card outline |
| Subtle surface | `SubtleFillColorSecondaryBrush` | Selected or supporting regions |
| Text primary | Inherited Fluent foreground | Titles, labels, values |
| Text secondary | `TextFillColorSecondaryBrush` | Descriptions and helper text |
| Text tertiary | `TextFillColorTertiaryBrush` | Empty-state and low-priority metadata |
| Accent | Fluent accent theme resources | Primary action, selection, focus and links |
| Caution | `SystemFillColorCautionBrush` | Actionable warning or validation state |
| Error | Fluent `InfoBar` error resources | Blocking errors and migration failure |

Rules:

- Use only Fluent `ThemeResource` or existing application resources in XAML. Do not introduce raw colors.
- Accent is reserved for selection, focus, navigation and the single primary action in a region.
- Status color must communicate real state. It is not decoration.
- Light and dark themes remain system-driven through `XamlControlsResources`; pages do not override the application theme.

## 3. Typography

### Scale

| Level | Resource or size | Weight | Usage |
|---|---|---|---|
| Page title | `WorkbenchPageTitleStyle`, 24 | SemiBold | One title per page |
| Section title | `SectionTitleStyle`, 17 | SemiBold | Card or work region title |
| Body | Fluent default control text | Regular | Forms, rows and explanations |
| Supporting | Fluent default with secondary foreground | Regular | Helper text, locations and summaries |
| Metric | Existing page-specific large Fluent text | SemiBold | Counts and operational totals |

Font stack is the Windows system UI font selected by WinUI, including the appropriate CJK fallback. No custom or serif font is introduced. Visible Chinese copy uses natural sentence breaks, `TextWrapping="WrapWholeWords"` where space is constrained, and tooltips for intentionally trimmed values.

## 4. Spacing & Layout

All layout spacing follows the existing 4-pixel rhythm.

| Intent | Values already used | Usage |
|---|---|---|
| Tight | 4, 6, 8 | Icon-label pairs, compact row content |
| Default | 10, 12 | Form groups, toolbar clusters, card stacks |
| Card | `CardPadding` = 16 | Standard panel interior |
| Page | `PagePadding` = `24,18,24,24` | Page frame |
| Section | 20, 24 | Separation between major work regions |

The application shell owns the window and keeps navigation fixed. Pages own their content scrolling. Areas uses a list-detail layout: the left group list keeps its own list scroll, while `DetailScrollViewer` is the right work surface's vertical scroll owner. `NarrowAreaLayout` and `WideAreaLayout` switch the master width at 1100 effective pixels; controls wrap or stack before labels clip. Primary content must remain readable at narrow desktop widths. Horizontal scrolling is allowed only inside explicitly named dense work regions, never as the page's default navigation model.

## 5. Components

### Workbench page header

- **Structure:** page title and short status or description, followed by a compact action cluster.
- **Variants:** normal, loading/status, action-bearing.
- **Spacing:** page padding with 8-12 spacing inside the header.
- **States:** normal, disabled action, loading status and error status.
- **Accessibility:** one clear page title; action buttons use visible text and `AutomationProperties.Name` where automated QA depends on them.
- **Motion:** none beyond native Fluent control feedback.
- **Layout:** cluster that wraps actions before text is clipped.

### Workbench card

- **Structure:** `Border` using `WorkbenchCardStyle`, section title, optional helper copy, then content.
- **Variants:** standard, zero-inner-padding list card and `MetricCardStyle`.
- **Spacing:** 16 padding, 8 radius, 1-pixel Fluent border, 8-12 inner stack spacing.
- **States:** normal, empty, loading, error and disabled controls.
- **Accessibility:** headings remain text, helper copy is not the only label, and empty/error states explain the next action.
- **Motion:** none.
- **Layout:** stack; any internal scroll owner must be named in XAML.

### Toolbar action

- **Structure:** Fluent button with optional Segoe Fluent icon and short Chinese label.
- **Variants:** `ToolbarButtonStyle` and one `PrimaryToolbarButtonStyle` per action region.
- **Spacing:** minimum height 32 and padding `12,5`.
- **States:** native default, pointer-over, pressed, focus, disabled and command-running disablement.
- **Accessibility:** visible label, no emoji, no icon-only destructive action, and no wrapped desktop label.
- **Motion:** native Fluent press feedback only.
- **Layout:** wrapping cluster.

### List-detail workbench

- **Structure:** selectable master list plus a detail `ScrollViewer` containing task cards.
- **Variants:** wide and narrow desktop states.
- **Spacing:** 12-16 between regions, 280 narrow master width and 360 wide master width.
- **States:** loading, empty list, selected detail, stale/error detail and disabled selection while refresh is authoritative.
- **Accessibility:** keyboard-selectable rows, descriptive tooltips for ellipsized text and deterministic focus order from master to detail.
- **Motion:** no custom transition.
- **Layout:** list-detail; the master list and detail viewer have separate named scroll jobs.

### Operational row

- **Structure:** primary label, location or reason metadata, optional note, and row-local actions.
- **Variants:** rule, current device, confirmed member, exception and pending audit change.
- **Spacing:** 8-12 cell gaps with a single sparse divider or card boundary.
- **States:** normal, selected, disabled, pending, empty and action error.
- **Accessibility:** long CJK labels wrap or trim with tooltip; row actions identify the target in automation help text when needed.
- **Motion:** none.
- **Layout:** grid on wide surfaces and readable vertical stack when constrained.

### Inline operational form

- **Structure:** label above control, optional helper text, validation message below, then action cluster.
- **Variants:** group, rule, device, note and filter editors.
- **Spacing:** 8 between label/control/message and 12 between fields.
- **States:** default, focused, disabled, validation error, saving and saved feedback.
- **Accessibility:** placeholders never replace labels; validation is visible text; keyboard order follows visual order.
- **Motion:** none.
- **Layout:** grid when wide and stack when narrow.

## 6. Motion & Interaction

Custom motion is intentionally absent. The operational UI uses native Fluent hover, pressed, focus, selection and disclosure transitions. Commands disable their initiating control while an authoritative load or write is running. Destructive actions require the existing confirmation-dialog pattern. Successful state changes refresh the affected list; failures retain current rows and show contextual status text. System reduced-motion preferences are inherently respected because no additional animation is introduced.

## 7. Depth & Surface

Depth strategy: borders plus subtle Fluent tonal surfaces. `WorkbenchCardStyle` uses the theme card fill, a one-pixel theme border and the shared 8-pixel radius. No custom shadows, glow, glass effect or decorative gradient is added. Nested card stacks are avoided when spacing or a row separator communicates the hierarchy more clearly.

## 8. Accessibility Constraints & Accepted Debt

### Constraints

- Target Windows Fluent accessibility behavior and WCAG 2.2 AA contrast through theme resources.
- Every interactive action remains keyboard reachable and has a visible focus state.
- Automated QA actions receive stable `AutomationProperties.Name` values.
- CJK copy must not clip at the supported narrow and wide desktop states.
- Long device names, locations, notes and rule descriptions wrap or expose full text by tooltip.
- Loading, empty and failure states are represented by text, not color alone.
- Destructive actions remain distinct from primary actions and require confirmation where data is removed.

### Accepted Debt

| Item | Location | Why accepted | Owner / Exit |
|---|---|---|---|
| Dense device data table uses an explicit horizontal work region | `DataPage.xaml` | The operational column set is inherently two-dimensional and predates this feature | Reassess when the data table is redesigned, not during area-group work |
| Some existing pages use `ProgressRing` rather than shape-matched skeletons | Existing desktop pages | Native WinUI feedback is consistent and replacing it is outside the approved feature scope | Revisit in a cross-page loading-state pass |
