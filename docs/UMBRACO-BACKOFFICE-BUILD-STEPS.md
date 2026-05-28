# Umbraco Backoffice Build Steps

Use this guide to create the first TMMOnline content model in the Umbraco backoffice.

Reference files:

- [docs/UMBRACO-DOCUMENT-TYPES.md](docs/UMBRACO-DOCUMENT-TYPES.md)
- [docs/UMBRACO-BACKOFFICE-BUILD-CHECKLIST.md](docs/UMBRACO-BACKOFFICE-BUILD-CHECKLIST.md)

## 1. Open the backoffice

1. Sign in to the Umbraco backoffice for the new TMMOnline app.
2. Go to **Settings**.
3. Confirm the current database is the TMMOnline database, not the older site.

## 2. Create the Page SEO composition

1. In **Settings**, open **Document Types**.
2. Click **...** or **Create** and choose **Composition**.
3. Name it **Page SEO**.
4. Set the alias to `pageSeo`.
5. Add a property group named **SEO**.
6. Add these properties:
   - `metaDescription` as **Textstring**
   - `metaTitle` as **Textstring**
   - `hideInNavigation` as **Checkbox**
7. Save.

## 3. Create the Page Content composition

1. Stay in **Settings** > **Document Types**.
2. Create another **Composition**.
3. Name it **Page Content**.
4. Set the alias to `pageContent`.
5. Add a property group named **Content**.
6. Add these properties:
   - `heroHeading` as **Textstring**
   - `introText` as **Textarea**
   - `leadText` as **Textstring**
   - `bodyText` as **Rich Text Editor**
   - `contentBlocks` as **Block List**
   - `publishedOn` as **Date Picker**
7. Save.

## 4. Create the Home Page doctype

1. Create a new **Document Type**.
2. Name it **Home Page**.
3. Set the alias to `homePage`.
4. Set **Permitted as root** to **Yes**.
5. Set **Is container** to **Yes**.
6. Leave **Is element type** disabled.
7. Add compositions:
   - `pageSeo`
   - `pageContent`
8. Set the allowed template and default template to **HomePage**.
9. Save.
10. Open the **Permissions** or **Structure** section and allow children:
   - `sectionPage`
   - `articlePage`

## 5. Create the Section Page doctype

1. Create a new **Document Type**.
2. Name it **Section Page**.
3. Set the alias to `sectionPage`.
4. Set **Permitted as root** to **No**.
5. Set **Is container** to **Yes**.
6. Leave **Is element type** disabled.
7. Add compositions:
   - `pageSeo`
   - `pageContent`
8. Set the allowed template and default template to **SectionPage**.
9. Save.
10. Allow children:
   - `sectionPage`
   - `articlePage`

## 6. Create the Article Page doctype

1. Create a new **Document Type**.
2. Name it **Article Page**.
3. Set the alias to `articlePage`.
4. Set **Permitted as root** to **No**.
5. Set **Is container** to **No**.
6. Leave **Is element type** disabled.
7. Add compositions:
   - `pageSeo`
   - `pageContent`
8. Set the allowed template and default template to **ArticlePage**.
9. Save.
10. Do not allow children.

## 7. Assign templates

1. Go to **Settings** > **Templates** if needed.
2. Confirm these templates exist and are linked to the matching doctypes:
   - `HomePage` -> `Views/HomePage.cshtml`
   - `SectionPage` -> `Views/SectionPage.cshtml`
   - `ArticlePage` -> `Views/ArticlePage.cshtml`
3. If templates are missing, create them before content import.

## 8. Build the content tree

1. Go to **Content**.
2. Create the root node using **Home Page**.
3. Add the main site sections as **Section Page** nodes.
4. Add article content under the relevant section using **Article Page**.
5. Use `hideInNavigation` for utility pages that should not appear in menus.

## 9. Quick validation

1. Open the root page and confirm the `HomePage` template renders.
2. Check that section pages and article pages render with their assigned templates.
3. Confirm the navigation shows only visible children.
4. Confirm the aliases match the migration mapping exactly.
5. Only start bulk migration after these checks pass.
