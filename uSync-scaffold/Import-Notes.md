# Import Notes

This scaffold is intentionally conservative.

## What it is

- A generated content-type and template mapping scaffold for the TMMOnline migration.
- A package-like folder structure that mirrors the content model we want in Umbraco.

## What it is not

- It is not a validated export from a live uSync installation.
- It is not guaranteed to match every uSync version's exact JSON schema.

## Recommended use

1. Review the aliases and property names.
2. Compare against your installed uSync version.
3. Import or recreate the types in a lower environment first.
4. Adjust only if your uSync schema requires additional keys.

## Stable aliases

- pageSeo
- pageContent
- homePage
- sectionPage
- articlePage

## Stability rule

Do not rename aliases after content migration starts.
