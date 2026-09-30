#!/usr/bin/env bash
# Asks real clipboard managers what they make of a secret copied by BitosX.
#
# For each clipboard manager that can be started:
#   1. control: a text copied WITHOUT the marker must turn up in its history. If it does not, the
#      clipboard manager is not recording here, and nothing can be said about it: INCONCLUSIVE.
#   2. a text copied WITH the marker, by the application's own clipboard code, must NOT turn up.
#
# Needs a display and a session bus; run it as
#   xvfb-run -a dbus-run-session -- bash tools/ci/clipboard-managers.sh
#
# The marker is a request, and clipboard managers differ in whether they honor it. What is found
# is reported. The exit code is 1 only if a clipboard manager that is known to honor the marker
# (see "expected" below) recorded a marked text: that would mean the marker is no longer set the
# way it has to be.

set -u

tool=(dotnet run -c Release --no-build --project tools/BitosX.ClipboardCheck --)
# Clipboard managers that honored the marker when this was written (2026-09-29).
expected=("Klipper")

regressed=0
summary=()

copy() { # copy [--unmarked]  -> prints the text that was copied
    "${tool[@]}" --hold 4 "$@" 2>/dev/null | tail -n 1
}

report() { # report <manager> <verdict> <detail>
    summary+=("$1: $2 — $3")
    echo "==> $1: $2 — $3"
}

check() { # check <manager> <function that prints its history>
    local manager=$1 history=$2 control marked
    control=$(copy --unmarked)
    if [[ $control != BITOSX-TEST-UNMARKED-* ]]; then
        report "$manager" "INCONCLUSIVE" "the control text could not be copied"
        return
    fi
    sleep 2
    if ! "$history" | grep -qF "$control"; then
        report "$manager" "INCONCLUSIVE" "it did not record the unmarked control text, so it is not recording here"
        return
    fi

    marked=$(copy)
    if [[ $marked != BITOSX-TEST-MARKED-* ]]; then
        report "$manager" "INCONCLUSIVE" "the marked text could not be copied"
        return
    fi
    sleep 2
    if "$history" | grep -qF "$marked"; then
        report "$manager" "RECORDED" "it recorded the unmarked control text AND the marked text: the marker was not honored"
        for known in "${expected[@]}"; do
            [[ $known == "$manager" ]] && regressed=1
        done
    else
        report "$manager" "HONORED" "it recorded the unmarked control text and did not record the marked text"
    fi
}

# ---- CopyQ ----

copyq_history() {
    local count i
    count=$(copyq count 2>/dev/null || echo 0)
    for ((i = 0; i < count; i++)); do
        copyq read "$i" 2>/dev/null
        echo
    done
}

if command -v copyq >/dev/null; then
    echo "--- CopyQ $(copyq --version 2>/dev/null | head -n 1)"
    copyq >/dev/null 2>&1 &
    copyq_pid=$!
    for _ in $(seq 1 20); do
        copyq eval "1" >/dev/null 2>&1 && break
        sleep 1
    done
    if copyq eval "1" >/dev/null 2>&1; then
        check "CopyQ" copyq_history
        copyq exit >/dev/null 2>&1
    else
        report "CopyQ" "INCONCLUSIVE" "it could not be started"
    fi
    kill "$copyq_pid" 2>/dev/null
else
    report "CopyQ" "INCONCLUSIVE" "it is not installed"
fi

# ---- Klipper (KDE) ----

klipper_history() {
    gdbus call --session --dest org.kde.klipper --object-path /klipper \
        --method org.kde.klipper.klipper.getClipboardHistoryMenu 2>/dev/null
}

if command -v klipper >/dev/null; then
    echo "--- Klipper $(klipper --version 2>/dev/null | head -n 1)"
    klipper >/dev/null 2>&1 &
    klipper_pid=$!
    for _ in $(seq 1 20); do
        klipper_history >/dev/null && break
        sleep 1
    done
    if klipper_history >/dev/null; then
        check "Klipper" klipper_history
    else
        report "Klipper" "INCONCLUSIVE" "it could not be started"
    fi
    kill "$klipper_pid" 2>/dev/null
else
    report "Klipper" "INCONCLUSIVE" "it is not installed"
fi

echo
echo "Summary"
printf '  %s\n' "${summary[@]}"
if [[ -n ${GITHUB_STEP_SUMMARY:-} ]]; then
    {
        echo "### Clipboard managers and the marker of a copied secret"
        printf -- '- %s\n' "${summary[@]}"
    } >> "$GITHUB_STEP_SUMMARY"
fi
exit "$regressed"
