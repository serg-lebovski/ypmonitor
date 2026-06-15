import sqlite3, sys
db = sys.argv[1]
c = sqlite3.connect(db)
cur = c.cursor()
cur.execute("INSERT INTO Clients (Name,Description,CreatedAt) VALUES (?,?,?)",
            ("Тест-Клиент", "демо", "2026-06-13T00:00:00+00:00"))
cid = cur.lastrowid
cur.execute("""INSERT INTO Servers
 (ClientId,Name,PhysicalAddress,IpAddress,Description,ApiKey,CreatedAt,LastOutcome,LastServerAvailable,LastBackupCount)
 VALUES (?,?,?,?,?,?,?,?,?,?)""",
            (cid, "SRV-DB1", "Стойка 5", "10.0.0.12", "тестовый", "testkey123",
             "2026-06-13T00:00:00+00:00", 0, 0, 0))
c.commit()
print("seeded client", cid)
