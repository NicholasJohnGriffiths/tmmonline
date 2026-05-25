# Umbraco Core Content Model Blueprint

These aliases map directly to the Razor templates already scaffolded in the app.

## Root Node

- Document type name: Home Page
- Alias: homePage
- Template: HomePage
- Allowed children:
  - Section Page
  - Article Page

## Section Page

- Document type name: Section Page
- Alias: sectionPage
- Template: SectionPage
- Allowed children:
  - Section Page
  - Article Page

## Article Page

- Document type name: Article Page
- Alias: articlePage
- Template: ArticlePage
- Allowed children: none

## Shared Properties (composition suggested)

Create a composition named Page SEO with:

- metaDescription (Textstring)

Create a composition named Page Content with:

- heroHeading (Textstring)
- introText (Textarea)
- leadText (Textstring)
- bodyText (Rich Text Editor)
- contentBlocks (Block List)
- publishedOn (Date Picker)

## Template Mapping

- HomePage => Views/HomePage.cshtml
- SectionPage => Views/SectionPage.cshtml
- ArticlePage => Views/ArticlePage.cshtml

## Navigation

The header partial builds menu links from visible children of the root node.

- Hide a page from nav by toggling Umbraco's Hide in navigation property.
