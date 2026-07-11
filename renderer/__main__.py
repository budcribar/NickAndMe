"""python -m renderer  → same as python -m cli"""
from cli.__main__ import main

if __name__ == "__main__":
    raise SystemExit(main())
