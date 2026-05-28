# Doctype Mapping (Legacy URL -> Umbraco)

Generated from migration-output/prioritized-pages.csv.

- Total mapped URLs: 134
- homePage: 1
- sectionPage: 21
- articlePage: 112

Mapping file:
- migration-output/doctype-mapping.csv

## Alias Rules Applied
- homePage: https://tmmonline.nz
- articlePage: all /article/* URLs
- sectionPage: all top-level sections, utility pages, and query variants

## Notes
- Query-string rates pages should redirect to canonical /rates where possible.
- Search page is mapped to sectionPage initially; swap to custom search template later if needed.
- Keep aliases stable during migration to avoid remapping work.
