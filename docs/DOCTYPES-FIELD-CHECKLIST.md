# Doctype Field Checklist

Use this after the creation sheet when building each type in Umbraco.

CSV:
- migration-output/doctypes-field-checklist.csv

## Composition: Page SEO
- metaDescription | Textstring | SEO | Optional
- metaTitle | Textstring | SEO | Optional
- hideInNavigation | Checkbox | SEO | Optional

## Composition: Page Content
- heroHeading | Textstring | Content | Optional
- introText | Textarea | Content | Optional
- leadText | Textstring | Content | Optional
- bodyText | Rich Text Editor | Content | Optional
- contentBlocks | Block List | Content | Optional
- publishedOn | Date Picker | Content | Optional

## Doctypes
- homePage: attach Page SEO + Page Content
- sectionPage: attach Page SEO + Page Content
- articlePage: attach Page SEO + Page Content

## Notes
- Use the same aliases everywhere.
- Add the compositions before assigning templates.
- Keep property group names stable to avoid migration mismatches.
