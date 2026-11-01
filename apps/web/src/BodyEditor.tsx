import { useEditor, EditorContent } from "@tiptap/react";
import StarterKit from "@tiptap/starter-kit";
import { useEffect } from "react";

export function BodyEditor({ initial }: { initial: string }) {
  const editor = useEditor({
    extensions: [StarterKit],
    content: `<p>${(initial || "")
      .split(/\n\n/)
      .map((p) => p.replace(/\n/g, "<br/>"))
      .join("</p><p>")}</p>`,
  });

  useEffect(() => {
    if (!editor) return;
    const html = `<p>${(initial || "")
      .split(/\n\n/)
      .map((p) => p.replace(/\n/g, "<br/>"))
      .join("</p><p>")}</p>`;
    editor.commands.setContent(html);
  }, [initial, editor]);

  return (
    <div style={{ padding: 8 }}>
      <EditorContent editor={editor} className="reader" />
    </div>
  );
}
