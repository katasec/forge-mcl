#!/usr/bin/env bash

set -euo pipefail

package_directory=${1:?usage: verify-core-package.sh <package-directory> <repository-commit>}
repository_commit=${2:?usage: verify-core-package.sh <package-directory> <repository-commit>}
package_id=Katasec.Forge.Mcl.Core
package_version=0.1.0
package_path="$package_directory/$package_id.$package_version.nupkg"

if [[ ! -f "$package_path" ]]; then
  echo "Missing package: $package_path" >&2
  exit 1
fi

entries=$(unzip -Z1 "$package_path")
for expected_entry in \
  "$package_id.nuspec" \
  "README.md" \
  "LICENSE.md" \
  "lib/net10.0/ForgeMission.Core.dll"; do
  if ! grep -Fxq "$expected_entry" <<<"$entries"; then
    echo "Missing package entry: $expected_entry" >&2
    exit 1
  fi
done

nuspec=$(unzip -p "$package_path" "$package_id.nuspec")
for expected_value in \
  "<id>$package_id</id>" \
  "<version>$package_version</version>" \
  "url=\"https://github.com/katasec/forge-mcl\"" \
  "commit=\"$repository_commit\"" \
  "<license type=\"file\">LICENSE.md</license>" \
  "<readme>README.md</readme>" \
  "id=\"Katasec.Forge.Mcl.Parser\" version=\"0.1.0\"" \
  "id=\"Katasec.Forge.Mcl.Scout\" version=\"0.1.0\""; do
  if ! grep -Fq "$expected_value" <<<"$nuspec"; then
    echo "Missing package metadata: $expected_value" >&2
    exit 1
  fi
done

echo "PASS: $package_id $package_version has the expected package metadata, contents, and internal dependencies."
