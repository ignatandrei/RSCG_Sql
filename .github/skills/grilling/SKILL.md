---
name: grilling
description: Provides guidance for grilling-related questions. Use when asked about grilling.
---

# Grilling


Interview the user relentlessly until you reach a shared understanding or max 30 questions. Map this as a **design tree**: every decision branches into the decisions that hang off it.



Work the tree in **rounds**. The **frontier** is every decision whose prerequisites are already settled: the questions you can ask _now_ without guessing at answers you haven't heard yet. Ask only as many frontier questions as fit within the remaining budget of 30 questions for the entire session: number each question and give your recommended answer. Then wait for the user's answers before the next round.



Format a round like so:



```

❓ **Q1** - **<question title>**: <question body, might be multiple paragraphs, including multiple choices>



➡️ <your recommended answer>



---



❓ **Q2** - **<question title>**: <question body, might be multiple paragraphs, including multiple choices>



➡️ <your recommended answer>

```



Word each question so "yes" accepts your recommended answer.



Each round the user answers reshapes the tree: settled decisions push the frontier outward and unblock questions that depended on them. Recompute the frontier and ask the next round only if fewer than 30 questions have been asked. A question whose answer depends on another question still open in this round belongs to a _later_ round, not this one.



Finding _facts_ is your job, never the user's. When a frontier question needs a fact from the environment (filesystem, tools, etc.), dispatch a sub-agent with a fresh context window to find it; don't ask the user for anything you could look up yourself. Don't block on it: a running exploration is an unsettled prerequisite, so only the questions downstream of it wait for the sub-agent to report; ask the rest of the frontier now. The _decisions_ are the user's: put each to them and wait.



Stop asking once 30 questions have been asked, even if the frontier is not empty. After processing the user's final answers, mark all remaining decisions unresolved, including those whose prerequisites are still unsettled; do not silently assume their answers. The interview is done when shared understanding is reached, the frontier is empty, or the 30-question limit is reached. Preserve the design tree for decisions handled within the limit. Do not act on it until the user confirms you have reached a shared understanding.

