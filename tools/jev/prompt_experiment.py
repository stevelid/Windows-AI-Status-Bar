"""Try alternative Jev question wordings on made-up final messages and print the probabilities.

Usage (needs the JEV_AI_API_KEY environment variable; the key is never printed):
    python tools/jev/prompt_experiment.py                      # set 1, every question
    python tools/jev/prompt_experiment.py set2 "kind2 (choice)" "blocked_on_user (v2)"
    python tools/jev/prompt_experiment.py set3 "kind2 (choice)"

Sets 1-3 are 30 made-up messages, each labelled with whether it should raise an alert (a stopped assistant)
or stay quiet. None of the text is real. The "kind2" choice and "blocked_on_user (v2)" wording match
JevTurnEndClassifier (PromptVersion 3); the other questions are the alternatives that lost (see D22 in
docs/PROGRESS.md). Change wording here first, compare, then update the classifier and bump PromptVersion.
"""
import json, os, sys, urllib.request

KEY = os.environ["JEV_AI_API_KEY"]
URL = "https://api.typesafe.ai/v1/systemone"

# label -> (should the user be alerted right away?, message). All text is made up.
MESSAGES = {
 "01 block: choose option": (True, "I found two ways to migrate the settings file. Option A keeps the old keys and adds the new ones (simpler, but leaves clutter). Option B rewrites everything to the new schema (cleaner, but existing users lose custom values unless we add a converter). Which option would you like me to take?"),
 "02 block: permission": (True, "The cleanup needs to delete the 14 old log folders under archive/. I haven't touched them yet. Do you want me to go ahead and delete them?"),
 "03 block: error/auth": (True, "I couldn't finish: the build server rejected my credentials (401) and I have no other way to authenticate. The changes are left uncommitted in the working folder. I need a valid token from you before I can push or run the pipeline."),
 "04 block: plan approval": (True, "Here is the plan: 1) split the parser into two classes, 2) add tests for the edge cases, 3) update the docs. This will touch about 12 files. Let me know if you'd like me to start, or change anything first."),
 "05 block: clarify": (True, "Before I start: do you want this for the Windows app only, or for the web version too? The approach differs, so I'd rather not guess."),
 "06 block: decision inside report": (True, "I reviewed the draft and found four issues, listed above. Three are simple fixes I have already made. One needs your decision before I can fix it: should the 30-day limit apply to weekends as well?"),
 "07 report + next steps (the false alarm)": (False, "Things to sort before it goes in the summary\n\n1. The new tolerance rows are a fresh assumption. They run from 4 to 20 units and earlier we agreed to allow none until the geometry was confirmed.\n   * What it's worth: without them the governing value rises by 6, so the limits fall by about 6.\n   * Where to check: the measured tests may already include some of it, so check it isn't counted twice.\n2. Edge path. The short-span test shows only a 16 reduction, and it is about 4 worse than the main route. It isn't governing, but it is worth one line saying it was checked.\n3. The measured values stop at 2 k. The extended values need a source or an assumption stated as such. There is plenty of margin, so it won't change the result.\n4. The daytime figure of 44 is lower than my mode of 47. Yours is the more cautious, so it's fine, but say which statistic you used.\n\nNext steps\n\n* Send me the geometry (heights and distances), or point me to the workbook. I can then run the check on the file and write the assumptions paragraph.\n* Then I can redraft the report short. It needs the limits table and the two operating conditions."),
 "08 done + optional offer": (False, "All 42 tests pass and the release build is clean. I've bumped the version to 2.4.0. Want me to also draft the release notes?"),
 "09 done plain": (False, "Finished. The report is saved as summary.docx, six pages, with the three charts embedded. No open issues."),
 "10 done + later suggestions": (False, "The refactor is complete. Two things you may want to consider later: the config loader could be cached, and the logging format is inconsistent between modules. Neither is urgent."),
 "11 rhetorical": (False, "Why does this matter? Because the cache key ignored the locale, so every French user saw English text. I've fixed it and added a test."),
 "12 placeholder, send data later": (False, "I've drafted the section using placeholder figures. When you have the final measurements, send them over and I'll swap them in. Everything else is complete."),
}

STATE_KEY = "assistant_final_message"

def noul(instr, yes, no):
    return {"type": "noul", "instructions": instr, "criteria": {"true": yes, "false": no}}

QUESTIONS = {
 # the first wording, as shipped
 "asks_user (v1)": noul(
  "The state holds the final message an AI coding assistant sent to the user when it stopped working. Does the message ask the user a question, or ask for a decision, choice, confirmation, permission or information that the assistant needs before it can continue? Rhetorical questions, and questions the message itself answers, do not count.",
  "The message ends by waiting for the user's answer, decision or approval.",
  "The message reports results or status and does not wait for an answer."),
 # the second wording, as now in the branch
 "blocked_on_user (v2)": noul(
  "The state holds the end of the final message an AI coding assistant sent to the user when it stopped working (assistant_final_message), and, when the message is long, how it began (assistant_message_start). Is the assistant now stopped, unable to make any further progress until the user answers a specific question or makes a decision? Count: a question it needs answered before it can go on, a request for permission or approval, or a blocker it cannot work around. Do NOT count: a report of results, findings or analysis; a review that lists issues; suggestions or recommendations; optional next steps; an offer to do more; or a list of information the user could supply later to improve or extend the work. If the message delivers something to read and only says what would help next, the answer is no.",
  "Work is paused and only the user's reply lets it continue.",
  "The assistant delivered a result, suggestions or optional next steps; the user can read it when convenient."),
 # shorter, framed around the user's interruption
 "interrupt (v3)": noul(
  "An AI assistant wrote the final message in the state and has stopped. Would a busy person want to be interrupted right now to answer or approve something, because the assistant will do nothing more until they respond? Answer no if the message is mainly a report, review or summary, even when it ends by listing next steps or things the person could send later.",
  "Yes: the assistant is stopped and waiting for the person's answer or approval.",
  "No: the person can read it later; the assistant is not stuck waiting."),
 # one exclusive choice about what kind of ending this is
 "kind (choice)": {"type": "choice",
  "instructions": "The state holds the final message an AI assistant sent when it stopped working. Which kind of ending is it?",
  "criteria": {
   "waiting_for_answer": "The assistant asked a specific question or needs a decision, and cannot continue without the reply.",
   "waiting_for_approval": "The assistant proposes a plan or action and is waiting for permission to proceed.",
   "stuck": "The assistant hit an error or missing access it cannot resolve and needs the user to fix something.",
   "report_with_next_steps": "The assistant delivered a review, findings or a result and lists next steps or inputs that would help later; nothing is blocked.",
   "finished_with_offer": "The work is done and the assistant offers optional extra work.",
   "finished": "The work is done with nothing further proposed.",
  }},
 # the choice again, with an explicit "carrying on" option
 "kind2 (choice)": {"type": "choice",
  "instructions": "The state holds the final message an AI assistant sent when it stopped working. Which kind of ending is it? Decide by whether the assistant is actually stopped waiting for the user, not by whether the message contains a question or mentions next steps.",
  "criteria": {
   "waiting_for_answer": "The assistant asked a specific question or needs a decision, and cannot continue without the reply.",
   "waiting_for_approval": "The assistant proposes a plan or action and is stopped, waiting for permission to proceed.",
   "stuck": "The assistant hit an error or missing access it cannot resolve and needs the user to fix something.",
   "carrying_on": "The assistant chose a way forward and is continuing by itself; it only invites the user to object if they disagree.",
   "report_with_next_steps": "The assistant delivered a review, findings or a result and lists next steps or inputs that would help later; nothing is blocked.",
   "finished_with_offer": "The work is done and the assistant offers optional extra work or asks if anything else is needed.",
   "finished": "The work is done with nothing further proposed.",
  }},
 # graded urgency
 "urgency (score)": {"type": "score",
  "instructions": "The state holds the final message an AI assistant sent when it stopped working. How urgently does the user need to act on it? Judge by whether the assistant is blocked, not by how much content there is.",
  "criteria": [
   "Nothing to do: finished, or only a report to read.",
   "Optional: next steps or offers the user may take up whenever they like.",
   "Needed soon: the assistant needs input, but can wait a while.",
   "Blocked now: the assistant is stopped until the user answers or approves.",
  ]},
}

MESSAGES2 = {
 "21 courtesy close": (False, "I've updated the three config files and re-run the checks; everything passes. Let me know if you want any changes."),
 "22 what next? (menu)": (True, "The import is done and verified. I can take this further in a few ways: A) add a progress bar, B) parallelise the loader, C) leave it as it is. Which of these should I do next?"),
 "23 want me to push?": (True, "I've prepared the commit with the message 'Fix locale cache key'. Want me to push it to the remote?"),
 "24 open-ended, no decision": (True, "I've explained the two approaches above and I don't have a strong preference. Let me know how you'd like to proceed."),
 "25 tests failing, will continue": (False, "Three tests still fail (listed above). I've narrowed them to the date parser and I'm continuing with that next; no action needed from you."),
 "26 waiting for go-ahead": (True, "Everything is staged and ready. I'll wait for your go-ahead before running the migration on the live database."),
 "27 does this look right?": (True, "I've rewritten the introduction to be shorter and more direct. Does this look right to you before I apply the same style to the other sections?"),
 "28 long report, quiet ending": (False, "Summary of the review.\n\nI checked the twelve modules against the checklist. Nine pass without comment. Module 4 has an unused import, module 7 logs a value it should not, and module 11 duplicates a helper from module 2. I fixed all three and re-ran the tests, which pass. The remaining open item is a naming inconsistency in the public API, which I have left alone because changing it would break callers. A full list of changes is in the CHANGES file."),
 "29 asks user to run something": (True, "I can't run the installer from here. Please run setup.exe on your machine and tell me what the last screen says, and I'll continue from there."),
 "30 fyi + question about optional": (False, "The export finished: 1,204 rows written to out.csv. By the way, the date column has a few blanks. I can fill them with the previous value if that would be useful."),
}

MESSAGES3 = {
 "31 proceeding unless told otherwise": (False, "I'll go with option A (keep the old keys) since it's the least risky, and I'm starting on it now. Shout if you'd rather I did B instead."),
 "32 anything else?": (False, "Done. The three files are updated and the build is green. Anything else you need?"),
 "33 must review before continuing": (True, "I need you to review the design doc at docs/design.md and tell me whether it's acceptable. I can't continue with the implementation until you have."),
 "34 short question": (True, "Which file should I edit?"),
 "35 should I continue?": (True, "That's the first half done. Should I continue?"),
 "36 mixed: result + blocking question at the end": (True, "The analysis is finished and the numbers are in the table above: revenue up 4%, costs flat, margin up 1.2 points. I noticed the Q3 export contains duplicate order IDs. I can't tell whether to drop or keep them, and it changes the totals. Which should I do?"),
 "37 apology, nothing needed": (False, "Sorry about the earlier mix-up with the file names. I've corrected them all and re-run the checks; everything is consistent now."),
 "38 long deliverable, ends with a question about optional": (False, "I've written the full onboarding guide (eight sections, about 3,000 words) and saved it as onboarding.md. It covers accounts, tooling, the first-week checklist, and who to ask for what. I also added a short glossary at the end. If you'd like, I can make a one-page version as well."),
}

def ask(text, questions):
    body = json.dumps({"model": "jev-latest", "state": {STATE_KEY: text}, "questions": questions}).encode()
    req = urllib.request.Request(URL, body, {"Content-Type": "application/json", "Authorization": "Bearer " + KEY})
    with urllib.request.urlopen(req, timeout=30) as r:
        return json.loads(r.read())

ALERT_KINDS = ("waiting_for_answer", "waiting_for_approval", "stuck")

def cell(ans):
    if ans["type"] == "noul": return f'{ans["noul"]:.2f}'
    if ans["type"] == "choice":
        mass = sum(ans["probabilities"].get(k, 0) for k in ALERT_KINDS)
        return f'{ans["choice"]} (alert mass {mass:.2f})'
    return f'{ans["score"]:.2f}'

def main():
    args = sys.argv[1:]
    messages = MESSAGES
    if args and args[0] in ("set2", "set3"):
        messages = MESSAGES2 if args[0] == "set2" else MESSAGES3
        args = args[1:]
    which = args or list(QUESTIONS)
    qs = {k: QUESTIONS[k] for k in which}
    names = list(qs)
    ids = {k: f"q{i}" for i, k in enumerate(names)}
    payload_questions = {ids[k]: qs[k] for k in names}
    print("expect | " + " | ".join(names))
    tokens = 0
    for label, (alert, text) in messages.items():
        res = ask(text, payload_questions)
        tokens += res["usage"]["input_tokens"] + res["usage"]["output_tokens"]
        cells = [cell(res["answers"][ids[k]]) for k in names]
        print(f'{"ALERT" if alert else "quiet"} | {label} | ' + " | ".join(cells))
    print("total tokens:", tokens)

main()
