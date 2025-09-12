# Layout system

This project uses [AvalonDock](https://github.com/xceedsoftware/wpftoolkit) to provide an IDE‑style layout with dockable tool windows and documents.

## Architecture

The `MainWindow` hosts a single `DockingManager`.  Tool windows are represented by `LayoutAnchorable` instances and documents by `LayoutDocument`.

Default tool windows:

- **Explorer** – left side tree used for navigation.
- **Output** – bottom log pane.
- **Search Results** – bottom placeholder for future search output.
- **Decompiler View** – main document showing decompiled code.

## Default layout

Explorer is docked left.  Decompiler View occupies the document area.  Output and Search Results share a tab group docked to the bottom.

## Layout serialization

Layout is saved to `%APPDATA%/Vibe/layout.config` on exit and restored on startup.  If loading fails, or the file is missing, the layout is reset to the baked‑in default found in `DefaultLayout.config`.

Use **View ▸ Reset Window Layout** to discard the saved layout and return to the default.

## Commands and shortcuts

| Command | Shortcut | Description |
| --- | --- | --- |
| View ▸ Explorer | Ctrl+Alt+E | Toggle Explorer tool window |
| View ▸ Output | Ctrl+Alt+O | Toggle Output window |
| View ▸ Search Results | Ctrl+Alt+S | Toggle Search Results window |
| View ▸ Reset Window Layout | Ctrl+Alt+R | Restore default layout |

## Adding new panes

1. Create the WPF control for the tool window or document.
2. Assign a unique `ContentId` in `DefaultLayout.config`.
3. Map the `ContentId` to the control inside `MainWindow.Serializer_LayoutSerializationCallback`.
4. Add a command and menu item if it should be toggleable.

## Troubleshooting

If the layout file becomes corrupted the application will fall back to the default after choosing **View ▸ Reset Window Layout** or deleting the file at the path above.
