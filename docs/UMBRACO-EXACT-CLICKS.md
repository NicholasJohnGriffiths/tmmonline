# Umbraco Exact Clicks

Use this when creating the first TMMOnline doctypes in the Umbraco backoffice.

Reference:

- [docs/UMBRACO-DOCUMENT-TYPES.md](docs/UMBRACO-DOCUMENT-TYPES.md)

## Click sequence

### 1. Open Document Types

1. Sign in to the Umbraco backoffice.
2. Click **Settings**.
3. Click **Document Types**.

### 2. Create composition: Page SEO

1. Click **...** or **Create**.
2. Choose **Composition**.
3. Name it `Page SEO`.
4. Set the alias to `pageSeo`.
5. Add a property group named `SEO`.
6. Add properties:
   - `metaDescription` - Textstring
   - `metaTitle` - Textstring
   - `hideInNavigation` - Checkbox
7. Click **Save**.

### 3. Create composition: Page Content

1. Click **...** or **Create**.
2. Choose **Composition**.
3. Name it `Page Content`.
4. Set the alias to `pageContent`.
5. Add a property group named `Content`.
6. Add properties:
   - `heroHeading` - Textstring
   - `introText` - Textarea
   - `leadText` - Textstring
   - `bodyText` - Rich Text Editor
   - `contentBlocks` - Block List
   - `publishedOn` - Date Picker
7. Click **Save**.

### 4. Create doctype: Home Page

1. Click **...** or **Create**.
2. Choose **Document Type**.
3. Name it `Home Page`.
4. Set the alias to `homePage`.
5. Enable **Permitted as root**.
6. Enable **Is container**.
7. Add compositions:
   - `pageSeo`
   - `pageContent`
8. Set the template to `HomePage`.
9. Save.
10. Allow children:
   - `sectionPage`
   - `articlePage`

### 5. Create doctype: Section Page

1. Click **...** or **Create**.
2. Choose **Document Type**.
3. Name it `Section Page`.
4. Set the alias to `sectionPage`.
5. Leave **Permitted as root** off.
6. Enable **Is container**.
7. Add compositions:
   - `pageSeo`
   - `pageContent`
8. Set the template to `SectionPage`.
9. Save.
10. Allow children:
   - `sectionPage`
   - `articlePage`

### 6. Create doctype: Article Page

1. Click **...** or **Create**.
2. Choose **Document Type**.
3. Name it `Article Page`.
4. Set the alias to `articlePage`.
5. Leave **Permitted as root** off.
6. Leave **Is container** off.
7. Add compositions:
   - `pageSeo`
   - `pageContent`
8. Set the template to `ArticlePage`.
9. Save.
10. Do not allow children.

### 7. Create root content node

1. Click **Content**.
2. Click **Create**.
3. Choose `Home Page`.
4. Name it for the site home page.
5. Save.

### 8. Create first content nodes

1. Under the root node, create your section pages using `Section Page`.
2. Under each section, create article pages using `Article Page`.
3. Mark utility pages with `hideInNavigation` if needed.

## Fast validation

1. Open the home page and confirm the `HomePage` template renders.
2. Open one section page and confirm it can contain child pages.
3. Open one article page and confirm it is a leaf page.
4. Check the menu only shows visible nodes.
