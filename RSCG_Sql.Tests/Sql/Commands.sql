-- @Name CreatePeople
CREATE TABLE people (
    id INTEGER PRIMARY KEY,
    name TEXT NOT NULL,
    age INTEGER NOT NULL
);
GO
-- @Name InsertPerson
-- @IdType long
INSERT INTO people(name, age) VALUES (@name, @age);
SELECT last_insert_rowid();
GO
-- @Name InsertPersonAndReturn
INSERT INTO people(name, age) VALUES (@name, @age);
-- @Returns Person
SELECT id AS Id, name AS Name, age AS Age FROM people WHERE id = last_insert_rowid();
GO
-- @Name GetPerson
-- @Returns Person
SELECT id AS Id, name AS Name, age AS Age FROM people WHERE id = @id;
GO
-- @Name UpdatePerson
UPDATE people SET age = @age WHERE id = @id;
GO
-- @Name DeletePerson
DELETE FROM people WHERE id = @id;
