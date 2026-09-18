#!/bin/sh
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
temporary_directory=$(mktemp -d "${TMPDIR:-/tmp}/check-history-test.XXXXXX")
trap 'rm -rf "$temporary_directory"' EXIT HUP INT TERM

missing_start=7c512a6cd53495d6bd0d446ab5dade1b7cab04b8
known_end=225eca7002386cba951654da3dc963c14b0ba18e

if ! (cd "$(git rev-parse --show-toplevel)" &&
  sh "$script_dir/check-history" "$missing_start" "$known_end"); then
  printf '%s\n' 'Missing-start history check failed.' >&2
  exit 1
fi
printf '%s\n' 'Missing-start history check passed.'

negative_repository=$temporary_directory/negative-repository
mkdir "$negative_repository"
(
  cd "$negative_repository"
  git init -q
  git config user.name 'History Check Test'
  git config user.email 'history-check-test@example.invalid'
  git commit --allow-empty -q -m 'Valid history check baseline'
  negative_start=$(git rev-parse HEAD)
  git commit --allow-empty -q -m 'Synthetic forbidden identity message' -m 'Co-authored-by: Example <example@example.invalid>'
  negative_end=$(git rev-parse HEAD)

  if sh "$script_dir/check-history" "$negative_start" "$negative_end" >"$temporary_directory/negative-output" 2>&1; then
    printf '%s\n' 'Forbidden identity message was accepted.' >&2
    exit 1
  fi
  grep -q 'History check failed at' "$temporary_directory/negative-output"
  printf '%s\n' 'Forbidden identity message rejected as expected.'
)

printf '%s\n' 'History check regression tests passed.'
