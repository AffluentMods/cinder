# Reads one request and dies without answering it.
import os
import sys

sys.stdin.readline()
sys.stderr.write("fatal: simulated crash\n")
sys.stderr.flush()
os._exit(3)
