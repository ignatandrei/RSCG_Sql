I found that the AI-generated Roslyn source generator produced code that used `item.GetType().GetProperty` to find each property name.
Because the SQL `SELECT` already names the columns, I wanted the generated code to access each property directly.

I tried to convince the AI to make these changes:

```text
read @final_instructions.md . Do not do anything !

Modify this code 
var property = item.GetType().GetProperty(reader.GetName(column), global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.Public | global::System.Reflection.BindingFlags.IgnoreCase);
Find the name of the property from the name of the what is selected into the select ( preserve the case)


throw an error when the column is not into the list
var columnName = column switch
                    {
                        0 => "id",
                        1 => "name",
                        _ => reader.GetName(column)
                    };
and get rid of property.getvalue and property.setvalue ( call directly the property)


I do not want
SetProperty(item.Id, reader.GetValue(column), value => item.Id = value);
Use direct column names



Could you optimize ConvertProperty<T> ? For example, store targetType into something to not calculate every time in the while for reader 



Could the Roslyn Code Generator inspect the type returned from Select and find properties that matches the column and the types and wrote the code accordingly ?


I still see  ConvertProperty . Please convert to the type of column

```

As you can see, this took a long time. I should have stated my requirements more directly.

