// IndexedDB storage for WebGL builds (DESIGN.md §8.1).
//
// Unity's plugin boundary is synchronous, but IndexedDB is not. Rather than marshal callbacks back
// into managed code, this keeps an in-memory mirror of every creation: reads answer from the mirror
// immediately, writes update it and queue a database put. A pending-write counter is exposed so the
// C# side can wait for real durability instead of assuming a returned call means the data is safe -
// which is the failure that loses a player's build when the tab closes.
//
// PlayerPrefs is deliberately avoided: its per-key size limits are far too small for a creation.

var BlockMarbleRunSaveStore = {

  $BMR: {
    DB_NAME: 'BlockMarbleRun',
    STORE: 'creations',
    VERSION: 1,

    db: null,
    ready: 0,
    failed: 0,
    pending: 0,
    mirror: {},

    alloc: function (str) {
      // stringToNewUTF8 is the current Emscripten name; allocateUTF8 is kept for older toolchains.
      if (typeof stringToNewUTF8 === 'function') return stringToNewUTF8(str);
      return allocateUTF8(str);
    },

    open: function () {
      try {
        var request = indexedDB.open(BMR.DB_NAME, BMR.VERSION);

        request.onupgradeneeded = function (event) {
          var db = event.target.result;
          if (!db.objectStoreNames.contains(BMR.STORE)) {
            db.createObjectStore(BMR.STORE);
          }
        };

        request.onsuccess = function (event) {
          BMR.db = event.target.result;
          BMR.loadAll();
        };

        request.onerror = function () {
          console.error('[BMR] IndexedDB open failed; saving is unavailable this session.');
          BMR.failed = 1;
          BMR.ready = 1;
        };
      } catch (e) {
        // Private browsing modes can throw outright rather than firing onerror.
        console.error('[BMR] IndexedDB unavailable: ' + e);
        BMR.failed = 1;
        BMR.ready = 1;
      }
    },

    loadAll: function () {
      var tx = BMR.db.transaction([BMR.STORE], 'readonly');
      var store = tx.objectStore(BMR.STORE);
      var cursor = store.openCursor();

      cursor.onsuccess = function (event) {
        var c = event.target.result;
        if (c) {
          BMR.mirror[c.key] = c.value;
          c.continue();
        } else {
          BMR.ready = 1;
        }
      };

      cursor.onerror = function () {
        console.error('[BMR] Failed to read existing creations.');
        BMR.failed = 1;
        BMR.ready = 1;
      };
    },

    put: function (key, value) {
      BMR.mirror[key] = value;

      if (!BMR.db) return;

      BMR.pending++;
      try {
        var tx = BMR.db.transaction([BMR.STORE], 'readwrite');
        tx.objectStore(BMR.STORE).put(value, key);
        tx.oncomplete = function () { BMR.pending--; };
        tx.onerror = function () {
          console.error('[BMR] Write failed for ' + key);
          BMR.pending--;
        };
        tx.onabort = function () { BMR.pending--; };
      } catch (e) {
        console.error('[BMR] Write threw for ' + key + ': ' + e);
        BMR.pending--;
      }
    },

    remove: function (key) {
      delete BMR.mirror[key];

      if (!BMR.db) return;

      BMR.pending++;
      try {
        var tx = BMR.db.transaction([BMR.STORE], 'readwrite');
        tx.objectStore(BMR.STORE).delete(key);
        tx.oncomplete = function () { BMR.pending--; };
        tx.onerror = function () { BMR.pending--; };
        tx.onabort = function () { BMR.pending--; };
      } catch (e) {
        BMR.pending--;
      }
    }
  },

  BMR_Init: function () {
    if (BMR.ready || BMR.db) return;
    BMR.open();
  },

  BMR_IsReady: function () {
    return BMR.ready;
  },

  BMR_HasFailed: function () {
    return BMR.failed;
  },

  BMR_PendingWrites: function () {
    return BMR.pending;
  },

  BMR_List: function () {
    var out = [];
    for (var key in BMR.mirror) {
      if (key.indexOf('thumb:') === 0) continue;
      out.push(key);
    }
    return BMR.alloc(JSON.stringify(out));
  },

  BMR_Load: function (keyPtr) {
    var key = UTF8ToString(keyPtr);
    var value = BMR.mirror[key];
    if (value === undefined || value === null) return 0;
    return BMR.alloc(value);
  },

  BMR_Save: function (keyPtr, valuePtr) {
    BMR.put(UTF8ToString(keyPtr), UTF8ToString(valuePtr));
  },

  BMR_Delete: function (keyPtr) {
    var key = UTF8ToString(keyPtr);
    BMR.remove(key);
    BMR.remove('thumb:' + key);
  },

  BMR_Free: function (ptr) {
    if (ptr) _free(ptr);
  }
};

autoAddDeps(BlockMarbleRunSaveStore, '$BMR');
mergeInto(LibraryManager.library, BlockMarbleRunSaveStore);
