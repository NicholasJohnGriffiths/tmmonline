# Umbraco Build in 30 Minutes

Use this single page to create the first TMMOnline doctypes in the Umbraco backoffice.

Reference files:

- [docs/UMBRACO-DOCUMENT-TYPES.md](docs/UMBRACO-DOCUMENT-TYPES.md)
- [docs/UMBRACO-EXACT-CLICKS.md](docs/UMBRACO-EXACT-CLICKS.md)
- [docs/UMBRACO-FIELD-BY-FIELD.md](docs/UMBRACO-FIELD-BY-FIELD.md)

## 1. Open Document Types

1. Sign in to the Umbraco backoffice.
2. Click **Settings**.
3. Click **Document Types**.

## 2. Create composition: `pageSeo`

Create a composition named `Page SEO` with alias `pageSeo`.

Add property group:

- `SEO`

Add fields:

- `metaDescription` - Textstring
- `metaTitle` - Textstring
- `hideInNavigation` - Checkbox

Save.

## 3. Create composition: `pageContent`

Create a composition named `Page Content` with alias `pageContent`.

Add property group:

- `Content`

Add fields:

- `heroHeading` - Textstring
- `introText` - Textarea
- `leadText` - Textstring
- `bodyText` - Rich Text Editor
- `contentBlocks` - Block List
- `publishedOn` - Date Picker

Save.

## 4. Create doctype: `homePage`

Create a document type named `Home Page` with alias `homePage`.

Set options:

- Permitted as root: Yes
- Is container: Yes
- Template: `HomePage`

Attach compositions:

- `pageSeo`
- `pageContent`

Allowed children:

- `sectionPage`
- `articlePage`

Save.

## 5. Create doctype: `sectionPage`

Create a document type named `Section Page` with alias `sectionPage`.

Set options:

- Permitted as root: No
- Is container: Yes
- Template: `SectionPage`

Attach compositions:

- `pageSeo`
- `pageContent`

Allowed children:

- `sectionPage`
- `articlePage`

Save.

## 6. Create doctype: `articlePage`

Create a document type named `Article Page` with alias `articlePage`.

Set options:

- Permitted as root: No
- Is container: No
- Template: `ArticlePage`

Attach compositions:

- `pageSeo`
- `pageContent`

Allowed children:

- none

Save.

## 7. Create the root content node

1. Click **Content**.
2. Click **Create**.
3. Choose `Home Page`.
4. Name it for the site home page.
5. Save.

## 8. Create the first pages

1. Add section pages under the home page using `Section Page`.
2. Add article pages under those sections using `Article Page`.
3. Use `hideInNavigation` on pages you do not want in menus.

## 9. Verify it works

1. Confirm the home page renders with the `HomePage` template.
2. Confirm `sectionPage` can contain more sections and articles.
3. Confirm `articlePage` has no children.
4. Confirm navigation hides pages flagged with `hideInNavigation`.

## Optional: Store Media in Azure Blob

If you want uploaded images/files in Azure Blob instead of local app storage, set these app settings (or environment variables):

- `Umbraco__Storage__AzureBlob__Media__ConnectionString`
- `Umbraco__Storage__AzureBlob__Media__ContainerName` (example: `media`)
- `Umbraco__Storage__AzureBlob__Media__ContainerRootPath` (optional)
- `Umbraco__Storage__AzureBlob__Media__VirtualPath` (example: `https://<storage-account>.blob.core.windows.net/media`)

When `ConnectionString` is provided, the app automatically uses Azure Blob for media.

## Fastest safe order

1. `pageSeo`
2. `pageContent`
3. `homePage`
4. `sectionPage`
5. `articlePage`
6. Root content node
