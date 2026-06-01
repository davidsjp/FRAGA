SELECT
    "cep",
    "uf",
    "cidade"
FROM "SBO_ELETROPAR_PRD"."FRETE_ELETROPAR_CEP"
WHERE "uf" = 'PR'
  AND UPPER("cidade") LIKE '%PONTA%'
ORDER BY "cep"
LIMIT 50;

SELECT
    "Cep",
    "UF",
    "Cidade",
    "CepOrigem",
    "CodTransportadora",
    "NomeTransportadora",
    "KgMaximo",
    "ValorMaximoNF",
    "StatusRegra"
FROM "SBO_ELETROPAR_PRD"."VW_ECO_FRETE_COMPLETO_DIRETO"
WHERE "UF" = 'PR'
  AND UPPER("Cidade") LIKE '%PONTA%'
  AND "StatusRegra" = 'OK'
ORDER BY "Cep", "CodTransportadora"
LIMIT 50;
