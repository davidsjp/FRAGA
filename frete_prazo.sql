-- Tabela para cadastrar prazo de entrega quando as tabelas de frete nao trazem SLA.
-- Regra recomendada: uma faixa de CEP por transportadora/CNPJ, sem sobreposicao.

CREATE TABLE "SBO_ELETROPAR_PRD"."FRETE_ELETROPAR_PRAZO" (
    "cod_transportadora" VARCHAR(20),
    "cnpj" VARCHAR(20),
    "uf" VARCHAR(2),
    "cidade" VARCHAR(100),
    "cep_inicial" VARCHAR(20),
    "cep_final" VARCHAR(20),
    "prazo_dias" INTEGER NOT NULL,
    "prioridade" INTEGER DEFAULT 100,
    "ativo" CHAR(1) DEFAULT 'Y',
    "observacao" VARCHAR(255)
);

CREATE INDEX "IDX_FRETE_ELETROPAR_PRAZO_01"
ON "SBO_ELETROPAR_PRD"."FRETE_ELETROPAR_PRAZO" ("ativo", "cod_transportadora", "cep_inicial", "cep_final");

CREATE INDEX "IDX_FRETE_ELETROPAR_PRAZO_02"
ON "SBO_ELETROPAR_PRD"."FRETE_ELETROPAR_PRAZO" ("ativo", "cnpj", "cep_inicial", "cep_final");

-- Exemplo para cadastrar prazo da BOMFIM em Fortaleza/CE.
-- Ajuste o prazo_dias antes de executar.
-- INSERT INTO "SBO_ELETROPAR_PRD"."FRETE_ELETROPAR_PRAZO" (
--     "cod_transportadora",
--     "cnpj",
--     "uf",
--     "cidade",
--     "cep_inicial",
--     "cep_final",
--     "prazo_dias",
--     "prioridade",
--     "ativo",
--     "observacao"
-- ) VALUES (
--     '000702',
--     '32808669001334',
--     'CE',
--     'FORTALEZA',
--     '60800000',
--     '60899999',
--     <PRAZO_EM_DIAS>,
--     10,
--     'Y',
--     'BOMFIM - Fortaleza/CE'
-- );
