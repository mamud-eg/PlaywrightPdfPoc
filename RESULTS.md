# RESULTS: Playwright as a wkhtmltopdf Replacement

## Samples

All three derive from **real** HTML/settings.xml harvested from the actual `ajoursystem-build-converterservice`
reporting service, via its `tests\DebuggingReportingService` debug harness (report type `FullRegistrationReport`).
No hand-authored/synthetic markup was used anywhere in this evaluation.

| Sample | Purpose | Content |
|---|---|---|
| `FullRegistrationReport` | Real single-registration report, as actually harvested. Portrait, `margin-bottom: 17mm` override. | 1 page |
| `FullRegistrationReport-MultiPage` | Stress test for genuine multi-page header/footer repeat behavior. The real per-registration block (already delimited by the template's own `page-break-after` div) repeated 12x. | 12–14 pages depending on strategy |
| `FullRegistrationReport-A2Landscape` | Stress test for non-default page size. Same real body/header/footer content; `settings.xml` swapped to `page-size: A2` + `orientation: Landscape`. | 1 page, A2 landscape |

`FullRegistrationReport`'s own reference PDF (from harvesting) is only 1 page, which can't validate repeat behavior across many pages — that's why `-MultiPage` exists as a targeted, real-content stress test rather than a fourth arbitrary sample.

## Results table

| Sample | Strategy | Success | Time (ms) | Size (KB) | Pages |
|---|---|---|---|---|---|
| FullRegistrationReport | wkhtmltopdf (baseline) | ✅ | ~2,700 | 44 | 1 |
| FullRegistrationReport | native | ✅ | ~1,500–2,000 | 187 | 1 |
| FullRegistrationReport | css-running | ✅ | ~1,500–1,700 | 347 | 1 |
| FullRegistrationReport | table-repeating | ✅ | ~1,400–2,500 | 347 | 1 |
| FullRegistrationReport-A2Landscape | wkhtmltopdf (baseline) | ✅ | ~2,700 | 44 | 1 |
| FullRegistrationReport-A2Landscape | native | ✅ | ~1,300–1,400 | 188 | 1 |
| FullRegistrationReport-A2Landscape | css-running | ✅ | ~1,400–1,700 | 348 | 1 |
| FullRegistrationReport-A2Landscape | table-repeating | ✅ | ~1,400–1,600 | 348 | 1 |
| FullRegistrationReport-MultiPage | wkhtmltopdf (baseline) | ✅ | ~2,600–2,800 | 83 | 12 |
| FullRegistrationReport-MultiPage | native | ✅ | ~1,300–1,500 | 222 | 13 |
| FullRegistrationReport-MultiPage | css-running | ✅ | ~1,500–1,700 | 383 | 14 |
| FullRegistrationReport-MultiPage | table-repeating | ✅ | ~1,400–2,600 | 381 | 12 |

Every cell succeeded. Timings vary run-to-run (±500ms) — Playwright's first conversion in a session pays a JIT/font-load warmup cost; treat these as ballpark, not precise benchmarks.

## Visual verdict (all confirmed by opening the generated PDFs directly)

- **1-page real sample**: all three Playwright strategies render header, footer, body, logo, and text correctly, arguably crisper than the wkhtmltopdf baseline (sharper logo edges, modern font rendering via the actual Google Fonts face rather than a fallback).
- **12-page multi-page stress test — the central question this POC exists to answer**: header and footer repeat correctly and consistently on **every single page**, for **all three** strategies, with no overlap, no drift, no degradation over many pages. This is the strongest and most important finding of this POC.
- **A2 landscape**: Playwright's `Format = "A2"` is accepted and renders a correctly-sized, correctly-oriented physical page. Untested before this round — confirmed working.

## Header/footer strategy comparison

| Strategy | Repeats every page? | Page numbers work? | Needs manual margin tuning? | Pages used (multi-page test) |
|---|---|---|---|---|
| **Native** (`HeaderTemplate`/`FooterTemplate`) | ✅ Yes | ✅ Yes (after remapping wkhtmltopdf's `page`/`topage` classes to Playwright's `pageNumber`/`totalPages`) | Yes — needs an explicit `Margin.Top`/`Bottom` reserved for header/footer height | 13 |
| **css-running** (`position:fixed`) | ✅ Yes — **contrary to the commonly-cited Chromium limitation**, verified empirically across 14 real pages | ❌ No — the original's page-number mechanism is a wkhtmltopdf-specific JS+querystring reload with no equivalent, and JS is disabled anyway | Yes — fixed-position elements are removed from flow, so body content needs matching padding reserved or it renders underneath the header (real bug found and fixed during this POC) | 14 |
| **table-repeating** (`<thead>`/`<tfoot>`) | ✅ Yes | ❌ No (same reason as css-running) | **No** — table layout naturally reserves header/footer space with zero manual margin math | 12 |

**Recommendation: `table-repeating` is the strongest of the two CSS-based strategies** — it needs no margin tuning, had no overlap bugs, and was the most space-efficient of all three strategies tested (12 pages vs. native's 13 and css-running's 14). But **native is the only strategy that gets page numbers working**, which most real business reports will want.

**Overall recommendation: use native `HeaderTemplate`/`FooterTemplate` as the primary strategy**, since page numbering is a hard requirement for most reports and its only real cost (external `<link>` stylesheets must be stripped, margin must be reserved manually) is a one-time, already-solved problem in this codebase (`HtmlMerger.BuildNativeTemplate`, `PdfGeneratorService.AdaptWkHtmlPageNumberClasses`). Reserve `table-repeating` as a fallback for any report where a header/footer turns out to need content page numbers can't support anyway (rare), since it's marginally more robust and space-efficient.

## Real bugs found and fixed while testing against real content

These were not hypothetical risks — every one of them broke the harness against the real harvested sample until fixed:

1. **Native template crash**: passing the harvested header/footer's full standalone `<html>` document straight into `HeaderTemplate`/`FooterTemplate` makes `Page.printToPDF` fail outright (`Protocol error: Printing failed`), not just render wrong. Fixed by extracting `<head>` assets + `<body>` inner HTML (`HtmlMerger.BuildNativeTemplate`).
2. **External stylesheet crash**: an external `<link rel="stylesheet">` (present in *every* real report's header/footer — a Google Fonts import) also crashes native template rendering outright, not just fails to apply. Fixed by stripping external stylesheet links before use. **This is the single most important finding for anyone porting this to production** — every real header/footer template in this codebase has this import, so native strategy is unusable without this fix.
3. **css-running overlap bug**: `position:fixed` removes the header/footer from the document's flow, so without reserving equivalent padding on the body content, the fixed header renders on top of (overlapping/garbling) the body text on page 1. Fixed by adding padding matching the native strategy's margins.
4. **No page-number equivalent for the original mechanism**: the original's `<span class="page">`/`<span class="topage">` are filled in by a JS function reading `window.location.search`, which is wkhtmltopdf's own per-page-reload mechanism — no direct equivalent exists in Chromium. Fixed for native strategy only, by remapping to Playwright's own `pageNumber`/`totalPages` placeholder classes.

## `settings.xml` keys and their Chromium equivalents

Only two keys were found in real report templates across the 13 report types surveyed in the source repo:

| Key | Chromium/Playwright equivalent | Notes |
|---|---|---|
| `orientation` | `PagePdfOptions.Landscape` | Direct equivalent, works. |
| `page-size` | `PagePdfOptions.Format` | Direct equivalent for standard sizes (A4, A2, Letter, etc.) — confirmed `"A2"` is accepted. |
| `margin-bottom` | `PagePdfOptions.Margin.Bottom` | Works, but needs a unit appended for bare numeric values (wkhtmltopdf treats `15` as millimeters; Playwright's `Margin` needs `"15mm"`). Precedence preserved: a report's own `margin-bottom` overrides whatever default margin the strategy would otherwise apply, matching the original service's flag-ordering behavior. |

No keys with **no** Chromium equivalent were found in the real templates surveyed — this is a smaller gap than the original plan anticipated, though only 13 of the real templates' `settings.xml` files were surveyed for keys (not all possible historical values that might exist in other report types not covered here).

**Static CLI flags with no Chromium equivalent** (not from settings.xml, but part of every original wkhtmltopdf invocation):
- `--disable-internal-links` — no Chromium equivalent found; any `<a href="#anchor">` in report content would remain clickable in the Playwright-generated PDF. Low risk unless reports rely on this being disabled for a specific reason.
- `--no-outline` — Playwright's `Outline` option defaults to `false` already, so no change needed.

## Image quality / file size

wkhtmltopdf's `--dpi 96 --lowquality --image-quality 80 --image-dpi 300` tuning produces dramatically smaller files than Chromium's PDF export, which has no equivalent lossy-recompression knob:

| Sample | wkhtmltopdf | Native | css-running/table-repeating |
|---|---|---|---|
| FullRegistrationReport | 44 KB | 187 KB (4.3x) | 347 KB (7.9x) |
| A2Landscape | 44 KB | 188 KB (4.3x) | 348 KB (7.9x) |
| MultiPage (12-14 pages) | 83 KB | 222 KB (2.7x) | ~382 KB (4.6x) |

Native strategy produces meaningfully smaller files than the two merged-document strategies (they embed the same background/company logo image twice as often per page across a `<head>` that's duplicated per merge, plus PrintBackground rendering overhead) — file size is a secondary reason to prefer native alongside page numbering. All Playwright output is substantially larger than wkhtmltopdf's aggressively-compressed baseline; if output size matters for storage/bandwidth (e.g. for a system exporting many reports to AjourBox), this is a real, unavoidable regression to budget for — Chromium's PDF export offers no direct lever to shrink embedded raster images the way `--image-quality`/`--image-dpi` did.

## Deployment cost note

The plan estimated the Chromium binary at ~150MB. The actual measured installed size on this Windows machine is **~415MB** for full Chromium (`pw.Chromium.LaunchAsync()`, used throughout this POC), or **~270MB** if the leaner `chromium-headless-shell` channel is used instead (`BrowserTypeLaunchOptions.Channel = "chromium-headless-shell"`) — a real cost-reduction lever worth evaluating for a production port if only headless PDF generation is needed and no other Chromium features (e.g. real browser UI screenshots) are required.

## Performance model note

This POC used a single browser process launched once (`PdfGeneratorService.CreateAsync`), with a **fresh `BrowserContext` per conversion job** — not a fresh browser process per job the way wkhtmltopdf inherently works (`Process.Start` per conversion). All Playwright timings above (~1.4–2.6s per conversion) already reflect this context-per-job model; a process-per-job Playwright variant (relaunching Chromium fully for every conversion) was not separately measured, since a production port would almost certainly keep a browser warm across jobs rather than deliberately reintroducing wkhtmltopdf's own cold-start cost. For reference, wkhtmltopdf's baseline (inherently process-per-job) took ~2.6–2.8s per conversion in this environment — comparable to or slower than Playwright's context-per-job numbers despite producing much smaller files, suggesting Playwright's context-reuse model is not a performance regression even before accounting for file-size/quality tradeoffs.

## Conclusion and recommendation

**Playwright is viable as a wkhtmltopdf replacement.** All three header/footer strategies work correctly, including on genuinely multi-page real content (the central risk this POC was built to test) — header and footer repeat correctly on every page for all three strategies, with no drift or degradation across 12-14 pages.

**Recommended approach for the real port: native `HeaderTemplate`/`FooterTemplate`**, because:
- It's the only strategy that supports page numbers, which most real reports need.
- It produces meaningfully smaller output files than the merged-document strategies.
- Its two failure modes (crash on raw full-document templates, crash on external stylesheets) are already understood and fixed in this codebase (`HtmlMerger.BuildNativeTemplate`).

**Real work required for a production port, beyond what this POC covers:**
- Strip external `<link rel="stylesheet">` tags from every real report's header/footer before passing to native templates — this affects every one of the 13 report types surveyed, not just the one tested here, since they all share the same Google Fonts import pattern.
- Decide on a header/footer height convention — this POC uses a fixed `20mm`/`15mm` approximation (`PdfGeneratorService.NativeHeaderHeight`/`DefaultFooterMargin`); a production port should measure real header/footer heights per report type rather than using one fixed guess across all of them, especially since it directly affects the `--disable-internal-links`-adjacent concern and overall page count (this POC's native strategy used 13 pages vs. table-repeating's 12 for identical content, entirely due to reserved-margin sizing).
- Budget for the output file size increase (2.7x–7.9x wkhtmltopdf's aggressively-compressed baseline) — there is no direct Chromium equivalent to `--image-quality`/`--image-dpi` tuning.
- Evaluate the `chromium-headless-shell` channel (~270MB vs ~415MB) if deployment footprint matters and no non-PDF browser features are needed.
