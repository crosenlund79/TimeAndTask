import { test } from "node:test";
import assert from "node:assert/strict";
import { deflateRawSync } from "node:zlib";
import { crc32, createZip, readZip } from "../app/zip.js";

test("round trip with UTF-8 names and binary data", async () => {
  const png = new Uint8Array([0x89, 0x50, 0x4e, 0x47, 0, 1, 2, 255]);
  const zip = createZip([{ path: "cards.json", data: '{"a":"Æble"}' }, { path: "attachments/WT-001/x-skærm.png", data: png }]);
  const files = await readZip(zip);
  assert.equal(new TextDecoder().decode(files.get("cards.json")), '{"a":"Æble"}');
  assert.deepEqual(files.get("attachments/WT-001/x-skærm.png"), png);
});

test("crc32 matches the standard check value", () => {
  assert.equal(crc32(new TextEncoder().encode("123456789")), 0xcbf43926);
});

test("reads deflated entries (ZIPs re-packed by other tools)", async () => {
  const text = "repeated ".repeat(200);
  const raw = new TextEncoder().encode(text);
  const deflated = deflateRawSync(raw);
  // Build a one-entry deflated ZIP by hand.
  const name = new TextEncoder().encode("note.txt");
  const local = Buffer.alloc(30);
  local.writeUInt32LE(0x04034b50, 0); local.writeUInt16LE(20, 4); local.writeUInt16LE(8, 8);
  local.writeUInt32LE(crc32(raw), 14); local.writeUInt32LE(deflated.length, 18); local.writeUInt32LE(raw.length, 22); local.writeUInt16LE(name.length, 26);
  const central = Buffer.alloc(46);
  central.writeUInt32LE(0x02014b50, 0); central.writeUInt16LE(20, 4); central.writeUInt16LE(20, 6); central.writeUInt16LE(8, 10);
  central.writeUInt32LE(crc32(raw), 16); central.writeUInt32LE(deflated.length, 20); central.writeUInt32LE(raw.length, 24); central.writeUInt16LE(name.length, 28);
  const end = Buffer.alloc(22);
  const centralOffset = 30 + name.length + deflated.length;
  end.writeUInt32LE(0x06054b50, 0); end.writeUInt16LE(1, 8); end.writeUInt16LE(1, 10); end.writeUInt32LE(46 + name.length, 12); end.writeUInt32LE(centralOffset, 16);
  const zip = new Uint8Array(Buffer.concat([local, name, deflated, central, name, end]));
  const files = await readZip(zip);
  assert.equal(new TextDecoder().decode(files.get("note.txt")), text);
});

test("damaged or foreign files are rejected with a readable message", async () => {
  await assert.rejects(readZip(new TextEncoder().encode("not a zip at all, sorry")), /not a ZIP/);
  const zip = createZip([{ path: "a.txt", data: "hello world" }]);
  zip[35] ^= 0xff; // flip a data byte
  await assert.rejects(readZip(zip), /checksum/);
});
