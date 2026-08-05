# DESIGN.md — UI Design System

> The visual law for every screen. The canonical reference is `docs/design/reference-overview.png` — every new page must look like it belongs in that screenshot. When a component isn't specified here, derive it from the closest pattern below; never invent a new visual style.

## 1. Direction in one line

Clean, light, data-dense admin console: dark navy sidebar, near-white content canvas, white bordered cards, one blue accent, soft tinted pills for status. Calm and professional — color is reserved for status and the primary action.

## 2. Stack

- **Tailwind CSS + shadcn/ui** (tokens below map directly to Tailwind slate/blue scales)
- **Icons:** lucide-react, 20px default, stroke 1.75
- **Charts:** Recharts, styled per §7
- **Font:** Inter (fallback: system-ui). Load once globally.

## 3. Color tokens

| Token | Hex | Tailwind | Use |
|---|---|---|---|
| `primary` | #2563EB | blue-600 | Buttons, active nav, links, focus rings, chart line 1 |
| `primary-hover` | #1D4ED8 | blue-700 | Hover on primary |
| `sidebar-bg` | #0F172A | slate-900 | Sidebar, full height |
| `sidebar-text` | #94A3B8 | slate-400 | Inactive nav items |
| `canvas` | #F8FAFC | slate-50 | Page background |
| `card` | #FFFFFF | white | All cards/panels |
| `border` | #E2E8F0 | slate-200 | Card borders, dividers, table rules |
| `text-heading` | #0F172A | slate-900 | Titles, values, primary text |
| `text-body` | #475569 | slate-600 | Body, labels |
| `text-muted` | #64748B | slate-500 | Secondary text, table headers, timestamps |

**Semantic (status) colors — used for pills, dots, deltas, alert severities:**

| Meaning | Text/dot | Soft bg | Maps to |
|---|---|---|---|
| Success / OK / Healthy / Low | #16A34A | #DCFCE7 | Monitoring OK, resolved, low priority |
| Warning / Medium | #D97706 | #FEF3C7 | Monitoring Warning, medium priority |
| Critical / High / Danger | #DC2626 | #FEE2E2 | Monitoring Critical, high priority, destructive |
| Info / In Progress | #2563EB | #DBEAFE | Info states, in-progress status |
| Neutral / Other | #64748B | #F1F5F9 | Closed, disabled, unknown |

Monitoring severity MUST use these exact semantics everywhere (boards, maps, charts, pills) — a Critical is always this red family, never an alternate.

**Chart categorical order:** #2563EB → #8B5CF6 → #F59E0B → #14B8A6 → #94A3B8.

## 4. Type scale

| Role | Size/weight | Color |
|---|---|---|
| Page title | 28px / 700 | text-heading |
| Page subtitle | 14px / 400 | text-muted |
| Card title | 16px / 600 | text-heading |
| KPI value | 30px / 700 | text-heading |
| Body / table cells | 14px / 400-500 | text-body |
| Labels, table headers | 13px / 500 | text-muted |
| Pills, captions | 12px / 500 | semantic |

Sentence case everywhere (no ALL-CAPS headers). Numbers in tables/KPIs use `tabular-nums`.

## 5. Layout & spacing

- **Shell:** fixed dark sidebar 232px left, full height; content area scrolls. Topbar inside content: page title + subtitle left, global search / notification bell (blue count badge) / help right. Page-level filters (date range etc.) right-aligned below topbar.
- **Sidebar structure:** logo top (white text, blue icon); nav items = icon + label, 40px tall, 8px radius; active = solid `primary` fill with white text; hover = slate-800. Optional boxed "Quick actions" group (slate-800 border, outlined buttons). User card pinned bottom (avatar, name, role, chevron).
- **Grid:** 24px page padding, 24px gap between cards. KPI row = 4 equal cards. Content rows mix 2/3 + 1/3 or 1/2 + 1/2.
- **Cards:** white, 1px `border`, **12px radius**, no shadow (or barely: `shadow-sm` max), 20-24px inner padding. Card header row = title left, action link/select right.
- **Density:** this is a working tool — tables are compact (44-48px rows), forms are two-column where sensible, whitespace is generous *around* cards, tight *inside* data.

## 6. Component rules

- **Buttons:** primary = solid blue, 8px radius; secondary = white with `border`; destructive = solid critical red; icon buttons ghost. Height 36-40px.
- **Status/priority pills:** soft bg + semantic text (per §3), 12px text, 6px radius, 2px/8px padding, no border. Used for ticket status, priority, alert severity, lifecycle states.
- **Status dots:** 8px filled circle + 13px semantic-colored text label (as in System health list).
- **KPI stat card:** left icon in 40px soft-tinted circle (icon in matching strong color) → label (13px muted) → value (30px bold) → delta line: arrow + percent + "from last week" — delta color reflects *sentiment*, not direction (fewer open tickets = green even though it's down).
- **Tables:** header row 13px muted, no background; 1px `border` row dividers; no zebra striping; row hover slate-50; ID column as muted mono-ish (`#INC-1043`); pills inline; right-align numeric columns. "View all" link (blue, 13px) in the card header, not the footer.
- **Forms:** shadcn inputs, 8px radius, `border` default, blue focus ring; labels above, 13px/500; errors in critical red 12px below field.
- **Toasts:** top-right, white card + semantic left accent.
- **Empty states:** centered icon in soft circle + one-line explanation + primary action. Never a bare "No data."
- **Modals/drawers:** modals for confirm/quick-create; right-side drawer (480px) for detail-peek from tables.

## 7. Charts (Recharts)

- Line/area: 2px line, `primary`; area fill = vertical gradient primary 15% → 0%; comparison series slate-400 2px, no fill. Dashed slate-200 horizontal gridlines only. Axis labels 12px muted, no axis lines. Legend bottom, dot markers. Tooltip = white card w/ border.
- Donut: 65% inner radius, center = bold total + muted label, categorical order from §3, legend right with value + percent.
- Metric charts (monitoring) reuse the same styling; threshold lines dashed in warning/critical colors.

## 8. Dark mode

Same hues, inverted neutrals: canvas slate-950, cards slate-900, borders slate-800, heading text slate-100, body slate-400. Sidebar unchanged (already dark). Semantic colors keep the same strong values; soft pill backgrounds become 15% alpha of the strong color. Charts: gridlines slate-800.

## 9. Per-area notes

- **Agent app** (tickets, assets, monitoring): the reference look, dense tables + boards.
- **Self-service portal:** same tokens, lighter density — bigger cards, larger type, max-width 960px centered, no dark sidebar (simple top nav) so end users don't see the admin shell.
- **Dashboards/status boards** (WP-3.9, 5.5): tiles are cards; device status = semantic fill tiles; alert rows use severity pill + timestamp muted.
- **Topology maps** (React Flow): nodes as mini-cards (white, border, 8px radius, icon + name + status dot); edges slate-300; live status recolors the dot/border only, not the whole node.

## 10. Quality floor (every screen)

Responsive to 1280px minimum (tables scroll horizontally below that); visible keyboard focus (blue ring); WCAG AA contrast on text; loading = skeleton blocks (slate-100 shimmer), never spinners inside cards; reduced motion respected; all timestamps in user-local time, muted color.
