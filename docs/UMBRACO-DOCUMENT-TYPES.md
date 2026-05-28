# Umbraco Core Content Model Blueprint

These document types are the first build set for the TMMOnline migration.
They map directly to the Razor templates already scaffolded in the app.
Create the compositions first, then the doctypes, then wire templates.

## Composition: Page SEO

- Alias: pageSeo
- Inherit from: none
- Properties:
  - metaDescription | Textstring | Group: SEO | Mandatory: No
  - metaTitle | Textstring | Group: SEO | Mandatory: No
  - hideInNavigation | Checkbox | Group: SEO | Mandatory: No

## Composition: Page Content

- Alias: pageContent
- Inherit from: none
- Properties:
  - heroHeading | Textstring | Group: Content | Mandatory: No
  - introText | Textarea | Group: Content | Mandatory: No
  - leadText | Textstring | Group: Content | Mandatory: No
  - bodyText | Rich Text Editor | Group: Content | Mandatory: No
  - contentBlocks | Block List | Group: Content | Mandatory: No
  - publishedOn | Date Picker | Group: Content | Mandatory: No

## Doctype: Home Page

- Document type name: Home Page
- Alias: homePage
- Variants: Invariant
- Is element type: No
- Is container: Yes
- Permitted as root: Yes
- Allowed template: HomePage
- Default template: HomePage
- Compositions:
  - Page SEO
  - Page Content
- Allowed children:
  - Section Page
  - Article Page
- Properties in this doctype:
  - None required beyond compositions

## Doctype: Section Page

- Document type name: Section Page
- Alias: sectionPage
- Variants: Invariant
- Is element type: No
- Is container: Yes
- Permitted as root: No
- Allowed template: SectionPage
- Default template: SectionPage
- Compositions:
  - Page SEO
  - Page Content
- Allowed children:
  - Section Page
  - Article Page
- Properties in this doctype:
  - None required beyond compositions

## Doctype: Article Page

- Document type name: Article Page
- Alias: articlePage
- Variants: Invariant
- Is element type: No
- Is container: No
- Permitted as root: No
- Allowed template: ArticlePage
- Default template: ArticlePage
- Compositions:
  - Page SEO
  - Page Content
- Allowed children: none
- Properties in this doctype:
  - None required beyond compositions

## Template Mapping

- HomePage => Views/HomePage.cshtml
- SectionPage => Views/SectionPage.cshtml
- ArticlePage => Views/ArticlePage.cshtml

## Recommended Creation Order

1. Create the Page SEO composition.
2. Create the Page Content composition.
3. Create Home Page, then attach compositions and template.
4. Create Section Page, then attach compositions and template.
5. Create Article Page, then attach compositions and template.
6. Create the root Home Page node in the content tree.
7. Create first-level section nodes under Home Page.

## Navigation

The header partial builds menu links from visible children of the root node.

- Hide a page from nav by toggling Umbraco's Hide in navigation property.
