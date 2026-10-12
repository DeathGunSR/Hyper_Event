SET NOCOUNT ON;
USE Holoo1;
GO
PRINT '=== Identity columns on POS tables ===';
SELECT OBJECT_NAME(c.object_id) AS TableName, c.name AS Col, c.is_identity, c.is_nullable
FROM sys.columns c
WHERE c.object_id IN (OBJECT_ID('dbo.Res_Facture'), OBJECT_ID('dbo.Rs_Facture'),
                      OBJECT_ID('dbo.Res_Fac_List'), OBJECT_ID('dbo.Rs_FacList'), OBJECT_ID('dbo.Res_DailyRep'))
  AND (c.is_identity = 1 OR c.name IN ('F_Code','F_Code_C','F_Date','ID_FCode'))
ORDER BY TableName, c.column_id;
GO
PRINT '=== Indexes on Res_Facture / Res_Fac_List / Rs_Facture / Rs_FacList ===';
SELECT OBJECT_NAME(i.object_id) AS TableName, i.name AS IndexName, i.type_desc, i.is_unique, i.is_primary_key,
       STUFF((SELECT ',' + c2.name FROM sys.index_columns ic2 JOIN sys.columns c2 ON c2.object_id=ic2.object_id AND c2.column_id=ic2.column_id
              WHERE ic2.object_id=i.object_id AND ic2.index_id=i.index_id AND ic2.is_included_column=0
              ORDER BY ic2.key_ordinal FOR XML PATH('')),1,1,'') AS KeyCols
FROM sys.indexes i
WHERE i.object_id IN (OBJECT_ID('dbo.Res_Facture'), OBJECT_ID('dbo.Res_Fac_List'), OBJECT_ID('dbo.Rs_Facture'), OBJECT_ID('dbo.Rs_FacList'))
ORDER BY TableName, i.index_id;
GO
PRINT '=== Modules referencing the ARCHIVE tables (Rs_FacList / Rs_Facture) ===';
SELECT o.type_desc, o.name FROM sys.sql_modules m JOIN sys.objects o ON o.object_id = m.object_id
WHERE m.definition LIKE '%Rs_FacList%' OR m.definition LIKE '%insert into Rs_Facture%' OR m.definition LIKE '%Rs_Facture%';
GO
PRINT '=== def: sp_Res_ShowFacture_Fac_List ===';
EXEC sp_helptext 'dbo.sp_Res_ShowFacture_Fac_List';
GO
PRINT '=== def: sp_Res_Fac_ListAppend ===';
EXEC sp_helptext 'dbo.sp_Res_Fac_ListAppend';
GO
