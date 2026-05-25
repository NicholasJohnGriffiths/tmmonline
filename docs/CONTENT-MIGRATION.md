# Content and Media Migration Checklist (tmmonline.nz)

Use this process to move content, structure, and images safely into the new Umbraco build.

## 1. Discovery and Inventory

1. Crawl all public URLs from https://tmmonline.nz.
2. Build a content map:
   - URL
   - Page title
   - Template/layout type
   - Required components (hero, cards, tables, forms, etc.)
3. Collect all media references (images, PDFs, downloadable files).

## 2. Information Architecture in Umbraco

1. Define document types for each page family.
2. Define compositions for reusable fields (SEO, open graph, metadata, CTA blocks).
3. Define block/grid editors for flexible content sections.
4. Define media folders matching site sections.

## 3. Razor and Frontend Build

1. Create master layout and partials in `Views/`.
2. Create template views per document type.
3. Port HTML and JavaScript behavior from the existing site into maintainable partials/components.
4. Keep external JS dependencies versioned and documented.

## 4. Media Migration

1. Download source media from the current site.
2. Preserve filenames where possible to simplify redirects and editorial QA.
3. Bulk upload into Umbraco media library with sensible folder structure.
4. Verify alt text and metadata for accessibility and SEO.

## 5. Content Migration

1. Manual migration for key pages first (home, top landing pages).
2. Script-assisted migration for large repetitive sections if needed.
3. Validate internal links and reference pickers.
4. Add 301 redirect list from old URLs to new URLs.

## 6. QA and Launch Readiness

1. Cross-device checks (desktop, tablet, mobile).
2. Performance checks for large images and JS bundles.
3. SEO checks:
   - Titles and meta descriptions
   - Canonical URLs
   - XML sitemap
4. Security checks:
   - HTTPS
   - Cookie and privacy compliance

## 7. Cutover Plan

1. Freeze content edits on legacy site.
2. Re-run final content/media delta migration.
3. Smoke-test on Azure staging slot.
4. Swap slots and monitor logs.

## Optional Automation Paths

- Use scripts to extract legacy HTML/content into CSV/JSON and import via Umbraco Content Delivery or Management APIs.
- Move media to dedicated object storage strategy if you expect significant growth.
