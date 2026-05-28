# Umbraco Field-by-Field Click Guide

Use this guide after creating the basic doctypes. It lists the exact fields to add in each composition and doctype.

Reference files:

- [docs/UMBRACO-DOCUMENT-TYPES.md](docs/UMBRACO-DOCUMENT-TYPES.md)
- [docs/DOCTYPES-FIELD-CHECKLIST.md](docs/DOCTYPES-FIELD-CHECKLIST.md)
- [docs/UMBRACO-EXACT-CLICKS.md](docs/UMBRACO-EXACT-CLICKS.md)

## 1. Composition: Page SEO

Create a composition named `Page SEO` with alias `pageSeo`.

Add property group:

- Name: `SEO`

Add these fields:

1. `metaDescription`
   - Label: Meta Description
   - Editor: Textstring
   - Mandatory: No
2. `metaTitle`
   - Label: Meta Title
   - Editor: Textstring
   - Mandatory: No
3. `hideInNavigation`
   - Label: Hide in Navigation
   - Editor: Checkbox
   - Mandatory: No

## 2. Composition: Page Content

Create a composition named `Page Content` with alias `pageContent`.

Add property group:

- Name: `Content`

Add these fields:

1. `heroHeading`
   - Label: Hero Heading
   - Editor: Textstring
   - Mandatory: No
2. `introText`
   - Label: Intro Text
   - Editor: Textarea
   - Mandatory: No
3. `leadText`
   - Label: Lead Text
   - Editor: Textstring
   - Mandatory: No
4. `bodyText`
   - Label: Body Text
   - Editor: Rich Text Editor
   - Mandatory: No
5. `contentBlocks`
   - Label: Content Blocks
   - Editor: Block List
   - Mandatory: No
6. `publishedOn`
   - Label: Published On
   - Editor: Date Picker
   - Mandatory: No

## 3. Doctype: Home Page

Create a document type named `Home Page` with alias `homePage`.

Attach compositions:

- `pageSeo`
- `pageContent`

Set properties/settings:

- Permitted as root: Yes
- Is container: Yes
- Is element type: No
- Template: `HomePage`
- Default template: `HomePage`

Allowed children:

- `sectionPage`
- `articlePage`

## 4. Doctype: Section Page

Create a document type named `Section Page` with alias `sectionPage`.

Attach compositions:

- `pageSeo`
- `pageContent`

Set properties/settings:

- Permitted as root: No
- Is container: Yes
- Is element type: No
- Template: `SectionPage`
- Default template: `SectionPage`

Allowed children:

- `sectionPage`
- `articlePage`

## 5. Doctype: Article Page

Create a document type named `Article Page` with alias `articlePage`.

Attach compositions:

- `pageSeo`
- `pageContent`

Set properties/settings:

- Permitted as root: No
- Is container: No
- Is element type: No
- Template: `ArticlePage`
- Default template: `ArticlePage`

Allowed children:

- none

## 6. Validation

After creating the fields, confirm:

1. All aliases match exactly.
2. The templates are attached correctly.
3. `homePage` is the only root doctype.
4. `sectionPage` can contain more `sectionPage` and `articlePage` children.
5. `articlePage` has no children.

## 7. Build Order

1. Page SEO fields.
2. Page Content fields.
3. Home Page doctype.
4. Section Page doctype.
5. Article Page doctype.
6. Root content node.
