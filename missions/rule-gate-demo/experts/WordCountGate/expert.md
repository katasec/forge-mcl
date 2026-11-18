---
name: WordCountGate
input: explanation to validate
output: validated explanation
kind: rule
check: word_count >= 50
onFail: Your explanation is too short. Write at least 50 words — include a concrete example.
---
