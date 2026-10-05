"""Isolated indexing process; temporary files and upstream logs stay request-local."""
import asyncio
import json
from pathlib import Path
import sys

if __package__:
    from .token_usage import collect_token_usage
else:
    from token_usage import collect_token_usage


async def index(directory: Path) -> dict:
    options = json.loads((directory / "request.json").read_text(encoding="utf-8"))
    with collect_token_usage(enabled=options["include_summaries"]) as usage:
        result = await index_document(directory, options)
    result["usage"] = {
        **usage,
        "model_id": options["model"],
        "input_tokens": usage["prompt_tokens"],
        "output_tokens": usage["completion_tokens"],
    }
    return result


async def index_document(directory: Path, options: dict) -> dict:
    source = directory / options["source"]
    if source.suffix == ".pdf":
        from pageindex.flash import page_index_flash
        result = await asyncio.to_thread(page_index_flash, str(source), summary=options["include_summaries"],
                                         summary_model=options["model"], optimize=False)
        if options["include_text"] or not result.get("structure"):
            import pypdfium2 as pdfium

            pages = []
            with pdfium.PdfDocument(str(source)) as pdf:
                for number in range(len(pdf)):
                    page = pdf[number]
                    text_page = page.get_textpage()
                    try:
                        pages.append(text_page.get_text_range())
                    finally:
                        text_page.close()
                        page.close()
            if not result.get("structure"):
                if not any(page.strip() for page in pages):
                    raise ValueError("PDF has no readable text; provide OCR Markdown instead")
                result["structure"] = [{"title": options["name"], "node_id": "0000",
                                        "start_index": 1, "end_index": len(pages)}]
                result["warnings"] = ["No section tree detected; returned one document node without a generated summary."]
            if options["include_text"]:
                add_page_text(result["structure"], pages)
        if not options["include_text"]:
            remove_text(result)
        result["doc_name"] = options["name"]
        return result

    from pageindex.page_index_md import md_to_tree, extract_nodes_from_markdown

    text = source.read_text(encoding="utf-8")
    nodes, lines = extract_nodes_from_markdown(text)
    needs_heading = not nodes or any(line.strip() for line in lines[:nodes[0]["line_num"] - 1])
    offset = 0
    if needs_heading:
        source.write_text("# Document\n\n" + text, encoding="utf-8")
        offset = 2
    result = await md_to_tree(
        str(source), if_thinning=False, if_add_node_id="yes",
        if_add_node_text="yes" if options["include_text"] else "no",
        if_add_node_summary="yes" if options["include_summaries"] else "no",
        if_add_doc_description="no", summary_token_threshold=200,
        model=options["model"],
    )
    result["doc_name"] = options["name"]
    result["source_line_offset"] = offset
    return result


def add_page_text(nodes, pages):
    for node in nodes:
        start = max(1, node.get("start_index", 1))
        end = min(len(pages), node.get("end_index", start))
        node["text"] = "\n\n".join(pages[start - 1:end])
        add_page_text(node.get("nodes", []), pages)


def remove_text(value):
    if isinstance(value, dict):
        value.pop("text", None)
        for child in value.values():
            remove_text(child)
    elif isinstance(value, list):
        for child in value:
            remove_text(child)


if __name__ == "__main__":
    directory = Path(sys.argv[1]).resolve()
    result = asyncio.run(index(directory))
    (directory / "result.json").write_text(json.dumps(result, ensure_ascii=False), encoding="utf-8")
