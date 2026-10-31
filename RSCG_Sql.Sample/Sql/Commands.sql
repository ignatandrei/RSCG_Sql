-- @Name CreatePeople
CREATE TABLE people (id INTEGER PRIMARY KEY, name TEXT NOT NULL);
GO
-- @Name InsertPerson
-- @IdType long
INSERT INTO people(name) VALUES (@name);
SELECT last_insert_rowid();
GO
-- @Name GetPeople
-- @Returns Person
SELECT id AS Id, name AS Name FROM people;
