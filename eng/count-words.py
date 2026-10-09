"""Count the words in manuscript files the way the revision budgets do: prose plus
code (fence lines themselves excluded), split on whitespace.

Usage:  python sample-code/eng/count-words.py Manuscript/Chapter-07-*.md [more files]
"""
import glob
import sys


def count(path):
    prose = code = 0
    in_fence = False
    with open(path, encoding='utf-8') as f:
        for line in f:
            if line.strip().startswith('```'):
                in_fence = not in_fence
                continue
            n = len(line.split())
            if in_fence:
                code += n
            else:
                prose += n
    return prose, code


def main(patterns):
    paths = [p for pattern in patterns for p in sorted(glob.glob(pattern))]
    if not paths:
        sys.exit('No files matched.')
    for path in paths:
        prose, code = count(path)
        print(f'{prose + code:6}  (prose {prose:5}, code {code:5})  {path}')


if __name__ == '__main__':
    main(sys.argv[1:] or ['Manuscript/*.md'])
