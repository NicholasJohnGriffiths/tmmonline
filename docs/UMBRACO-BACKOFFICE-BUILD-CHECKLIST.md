# Umbraco Backoffice Build Checklist

Use this checklist to create the TMMOnline content model manually in the Umbraco backoffice.

Reference blueprint:

- [docs/UMBRACO-DOCUMENT-TYPES.md](docs/UMBRACO-DOCUMENT-TYPES.md)
- [docs/UMBRACO-BACKOFFICE-BUILD-STEPS.md](docs/UMBRACO-BACKOFFICE-BUILD-STEPS.md)

## Before You Start

1. Sign in to the Umbraco backoffice.
2. Confirm the site is using the new TMMOnline database.
3. Do not change aliases after content migration begins.
4. Keep templates and aliases aligned with the Razor files already scaffolded.

## Step 1. Create Compositions

### 1.1 Page SEO composition

Create a composition with alias `pageSeo` and add:

- `metaDescription` - Textstring
- `metaTitle` - Textstring
- `hideInNavigation` - Checkbox

Use a group named `SEO`.

### 1.2 Page Content composition

Create a composition with alias `pageContent` and add:

- `heroHeading` - Textstring
- `introText` - Textarea
- `leadText` - Textstring
- `bodyText` - Rich Text Editor
- `contentBlocks` - Block List
- `publishedOn` - Date Picker

Use a group named `Content`.

## Step 2. Create Document Types

### 2.1 Home Page

Create document type:

- Name: `Home Page`
- Alias: `homePage`
- Variants: Invariant
- Permitted as root: Yes
- Is container: Yes
- Is element type: No
- Default template: `HomePage`
- Allowed template: `HomePage`

Attach compositions:

- `pageSeo`
- `pageContent`

Allowed children:

- `sectionPage`
- `articlePage`

### 2.2 Section Page

Create document type:

- Name: `Section Page`
- Alias: `sectionPage`
- Variants: Invariant
- Permitted as root: No
- Is container: Yes
- Is element type: No
- Default template: `SectionPage`
- Allowed template: `SectionPage`

Attach compositions:

- `pageSeo`
- `pageContent`

Allowed children:

- `sectionPage`
- `articlePage`

### 2.3 Article Page

Create document type:

- Name: `Article Page`
- Alias: `articlePage`
- Variants: Invariant
- Permitted as root: No
- Is container: No
- Is element type: No
- Default template: `ArticlePage`
- Allowed template: `ArticlePage`

Attach compositions:

- `pageSeo`
- `pageContent`

Allowed children:

- none

## Step 3. Create Templates

Verify the following templates exist and are assigned to the matching doctypes:

- `HomePage` -> `Views/HomePage.cshtml`
- `SectionPage` -> `Views/SectionPage.cshtml`
- `ArticlePage` -> `Views/ArticlePage.cshtml`

## Step 4. Build the Content Tree

1. Create the root content node using `Home Page`.
2. Create the primary site sections under the root using `Section Page`.
3. Create article entries under the relevant section using `Article Page`.
4. Hide utility pages from navigation using `hideInNavigation` when needed.

## Step 5. Validate Before Migration

1. Open the root page and confirm the template renders.
2. Check that headings, body text, and media fields display correctly.
3. Confirm the navigation only shows visible children.
4. Confirm article nodes can be nested under sections.
5. Confirm the page aliases match the migration mapping file.

## Step 6. Migration Order

1. Build the compositions.
2. Build the doctypes.
3. Create the root home node.
4. Create the top-level sections.
5. Migrate Phase 1 pages.
6. Migrate Phase 2 article batches.
7. Finish with redirects and query-variant cleanup.

## Notes

- Keep field aliases exactly as written.
- If you later add more page families, add a new doctype rather than overloading `sectionPage` or `articlePage`.
- Do not change template names unless you also update the Razor file names.
