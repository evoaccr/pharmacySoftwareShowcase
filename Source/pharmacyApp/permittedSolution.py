import sys
import ddddocr


def recognize_image(image_path: str) -> str:

    ocr = ddddocr.DdddOcr(show_ad=False)
    ocr.set_ranges("0123456789o")

    with open(image_path, "rb") as f:
        text = ocr.classification(f.read())

    text = text.replace("o", "0").replace("O", "0")

    return text


def main():
    if len(sys.argv) != 2:
        print("Usage: python imageDisplay.py <image_path>", file=sys.stderr)
        sys.exit(2)

    try:
        text = recognize_image(sys.argv[1])

        # stdout contains ONLY the final result for C#.
        print(text, flush=True)

    except Exception as exc:
        print(str(exc), file=sys.stderr, flush=True)
        sys.exit(1)


if __name__ == "__main__":
    main()