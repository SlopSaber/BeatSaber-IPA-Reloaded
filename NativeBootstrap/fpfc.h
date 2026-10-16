#ifndef BSIPA_FPFC_H
#define BSIPA_FPFC_H
#include "../util/paths.h"
void bsipa_configure_fpfc(DoorstopPaths const *paths, char_t **command, char **narrow_command);
void bsipa_cleanup_fpfc(void);
void bsipa_record_hook(char const *name, bool_t installed);
void bsipa_finish_hooks(void);
#endif
