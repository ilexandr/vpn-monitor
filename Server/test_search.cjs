const fs = require("node:fs");
const vm = require("node:vm");
const assert = require("node:assert/strict");
const source = fs.readFileSync(__dirname + "/static/app.js", "utf8");
const start = source.indexOf("function filteredPeers()");
const end = source.indexOf("\nfunction ", start + 1);
assert(start >= 0 && end > start);
const context = {
    peers: ["PrestigBH_Inna","PrestigBH_Kassa2","PrestigBH_Sasha","PrestigBH_srv","PrestigStroyka_pc","PrestigStroyka_srv","PrestigStroyka_upr"].map(name => ({ name, groups: [], status: "running" })),
    me: { is_admin: true }, selectedGroup: null, selectedFilter: "all",
    searchText: "Prestig BH", isOnline: p => p.status === "running", endpointHost: x => x || "",
    comparePeers: () => 0
};
vm.createContext(context);
vm.runInContext(source.slice(start, end), context);
assert.equal(vm.runInContext("filteredPeers().length", context), 4);
context.searchText = "  BH   prestig srv ";
assert.equal(vm.runInContext("filteredPeers().length", context), 1);
console.log("PASS: Web multiword search using actual filteredPeers function.");