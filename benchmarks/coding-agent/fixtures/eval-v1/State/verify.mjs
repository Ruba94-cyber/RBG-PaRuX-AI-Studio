import { migrateV1, acceptSnapshot, resumeJournal } from "./state.mjs";
const id = process.argv[2];
const req = (c,m) => { if(!c) throw new Error(m); };

if (id === "migration-preserve-unknown") {
  const src = {version:1,theme:"dark",custom:{x:1},cloudFallback:false};
  const out = migrateV1(src);
  req(out.version === 2 && out.theme === "dark","core fields");
  req(JSON.stringify(out.custom) === JSON.stringify({x:1}) && out.cloudFallback === false,"unknown fields preserved");
} else if (id === "migration-reject-future") {
  req(acceptSnapshot({version:3},2) === false,"future version rejected");
  req(acceptSnapshot({version:2},2) === true,"current version accepted");
} else if (id === "recovery-resume-journal") {
  const out = resumeJournal([
    {id:"a",status:"completed",attempts:1},
    {id:"b",status:"pending",attempts:0},
    {id:"c",status:"failed",attempts:1}
  ]);
  req(out[0].status === "completed" && out[0].attempts === 1,"completed untouched");
  req(out[1].status === "completed" && out[1].attempts === 1,"pending executed once");
  req(out[2].status === "completed" && out[2].attempts === 2,"failed retried once");
  const again = resumeJournal(out);
  req(JSON.stringify(again) === JSON.stringify(out),"second pass idempotent");
} else {
  throw new Error(`unknown case ${id}`);
}
console.log("PASS");
