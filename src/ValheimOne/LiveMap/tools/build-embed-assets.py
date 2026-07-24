#!/usr/bin/env python3

from pathlib import Path
import sys


ROOT_SELECTOR = "#valheimone-embed-root"
ROOT_URL_PREFIXES = (
    "/api/",
    "/tiles/",
    "/assets/",
    "/base.png",
    "/fog.png",
    "/favicon.ico",
)
UNSCOPED_BLOCKS = (
    "@font-face",
    "@keyframes",
    "@-webkit-keyframes",
)


def find_block_end(css, opening_brace):
    depth = 1
    index = opening_brace + 1
    quote = None
    while index < len(css):
        character = css[index]
        if quote is not None:
            if character == "\\":
                index += 2
                continue
            if character == quote:
                quote = None
        elif css.startswith("/*", index):
            comment_end = css.find("*/", index + 2)
            if comment_end < 0:
                raise ValueError("unterminated CSS comment")
            index = comment_end + 2
        elif character in ("'", '"'):
            quote = character
        elif character == "{":
            depth += 1
        elif character == "}":
            depth -= 1
            if depth == 0:
                return index
        index += 1
    raise ValueError("unterminated CSS block")


def find_rule_delimiter(css, start):
    index = start
    quote = None
    parentheses = 0
    brackets = 0
    while index < len(css):
        character = css[index]
        if quote is not None:
            if character == "\\":
                index += 2
                continue
            if character == quote:
                quote = None
        elif css.startswith("/*", index):
            comment_end = css.find("*/", index + 2)
            if comment_end < 0:
                raise ValueError("unterminated CSS comment")
            index = comment_end + 2
        elif character in ("'", '"'):
            quote = character
        elif character == "(":
            parentheses += 1
        elif character == ")":
            parentheses -= 1
        elif character == "[":
            brackets += 1
        elif character == "]":
            brackets -= 1
        elif parentheses == 0 and brackets == 0 and character in ("{", ";"):
            return index
        index += 1
    return len(css)


def split_selector_list(selectors):
    parts = []
    start = 0
    index = 0
    quote = None
    parentheses = 0
    brackets = 0
    while index < len(selectors):
        character = selectors[index]
        if quote is not None:
            if character == "\\":
                index += 2
                continue
            if character == quote:
                quote = None
        elif selectors.startswith("/*", index):
            comment_end = selectors.find("*/", index + 2)
            if comment_end < 0:
                raise ValueError("unterminated CSS comment")
            index = comment_end + 2
        elif character in ("'", '"'):
            quote = character
        elif character == "(":
            parentheses += 1
        elif character == ")":
            parentheses -= 1
        elif character == "[":
            brackets += 1
        elif character == "]":
            brackets -= 1
        elif character == "," and parentheses == 0 and brackets == 0:
            parts.append(selectors[start:index])
            start = index + 1
        index += 1
    parts.append(selectors[start:])
    return parts


def split_leading_trivia(header):
    index = 0
    while index < len(header):
        if header[index].isspace():
            index += 1
            continue
        if header.startswith("/*", index):
            comment_end = header.find("*/", index + 2)
            if comment_end < 0:
                raise ValueError("unterminated CSS comment")
            index = comment_end + 2
            continue
        break
    return header[:index], header[index:]


def scope_selector(selector):
    leading = selector[: len(selector) - len(selector.lstrip())]
    trailing = selector[len(selector.rstrip()):]
    value = selector.strip()
    for page_root in (":root", "html", "body"):
        if value == page_root:
            value = ROOT_SELECTOR
            break
        if value.startswith(page_root):
            following = value[len(page_root):len(page_root) + 1]
            if following in ("", ".", "#", ":", "[", " ", ">", "+", "~"):
                value = ROOT_SELECTOR + value[len(page_root):]
                break
    else:
        value = ROOT_SELECTOR + " " + value
    return leading + value + trailing


def scope_rule_header(header):
    trivia, selectors = split_leading_trivia(header)
    return trivia + ",".join(
        scope_selector(selector) for selector in split_selector_list(selectors)
    )


def scope_css(css):
    output = []
    position = 0
    while position < len(css):
        delimiter = find_rule_delimiter(css, position)
        if delimiter == len(css):
            output.append(css[position:])
            break
        if css[delimiter] == ";":
            output.append(css[position:delimiter + 1])
            position = delimiter + 1
            continue

        header = css[position:delimiter]
        block_end = find_block_end(css, delimiter)
        block = css[delimiter + 1:block_end]
        _, significant_header = split_leading_trivia(header)
        lowered_header = significant_header.lstrip().lower()
        if lowered_header.startswith(UNSCOPED_BLOCKS):
            output.append(header + "{" + block + "}")
        elif lowered_header.startswith("@"):
            output.append(header + "{" + scope_css(block) + "}")
        else:
            output.append(scope_rule_header(header) + "{" + block + "}")
        position = block_end + 1
    return "".join(output)


def build_embed_fragment(index_html):
    body_open = index_html.index("<body>") + len("<body>")
    body_close = index_html.rindex("</body>")
    body = index_html[body_open:body_close]

    footer_open = body.index('    <footer class="sidebar-footer">')
    footer_close = body.index("    </footer>", footer_open) + len("    </footer>")
    body = body[:footer_open] + body[footer_close:]
    body = "\n".join(line.rstrip() for line in body.splitlines())

    return '<div id="valheimone-embed-root">' + body + "\n</div>\n"


def build_embed_javascript(app_javascript):
    output = app_javascript
    for quote in ('"', "'"):
        for prefix in ROOT_URL_PREFIXES:
            output = output.replace(quote + prefix, quote + prefix[1:])
    return output


def main():
    livemap_dir = Path(__file__).resolve().parent.parent
    web_dir = livemap_dir / "web"
    index_html = (web_dir / "index.html").read_text(encoding="utf-8")
    app_javascript = (web_dir / "app.js").read_text(encoding="utf-8")
    app_css = (web_dir / "app.css").read_text(encoding="utf-8")

    (web_dir / "embed.html").write_text(
        build_embed_fragment(index_html),
        encoding="utf-8",
        newline="\n",
    )
    (web_dir / "app.embed.js").write_text(
        build_embed_javascript(app_javascript),
        encoding="utf-8",
        newline="\n",
    )
    (web_dir / "app.embed.css").write_text(
        scope_css(app_css),
        encoding="utf-8",
        newline="\n",
    )


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError) as error:
        print("Failed to build embed assets: {}".format(error), file=sys.stderr)
        sys.exit(1)
