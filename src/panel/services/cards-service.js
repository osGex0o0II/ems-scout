'use strict';

function createCardsService(options) {
  const {
    loadCards,
    loadFilterOptions,
  } = options;

  return {
    loadCards,
    loadFilterOptions,
  };
}

module.exports = { createCardsService };
