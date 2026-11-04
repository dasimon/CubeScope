// Scope design tokens applied to PrimeVue Aura.
//
// One chromatic family: the three faces of the cube mark (emerald ramp) — the primary
// colour, the "success" state and the selection all come from it. Two colours are allowed
// outside that family: amber for anything that means "production" or "late", coral for
// errors. Everything else is ink: a cool slate pulled towards the favicon's deep teal,
// never neutral grey. The same values live in `style.css` as `--scope-*` variables for
// the parts PrimeVue does not style (dockview, Monaco host, hand-written markup).
import { definePreset } from '@primeuix/themes'
import Aura from '@primeuix/themes/aura'

const cube = {
  50: '#ECFDF5',
  100: '#D1FAE5',
  200: '#A7F3D0',
  300: '#6EE7B7',
  400: '#34D399',
  500: '#10B981',
  600: '#059669',
  700: '#047857',
  800: '#065F46',
  900: '#064E3B',
  950: '#022C22',
}

// Production / late / warning.
const amber = {
  50: '#FDF8EB',
  100: '#FAEEC9',
  200: '#F7DF9A',
  300: '#F4CC67',
  400: '#F0B33E',
  500: '#D9951F',
  600: '#B57717',
  700: '#8F5B14',
  800: '#6E4614',
  900: '#553613',
  950: '#2E1C08',
}

// Errors.
const coral = {
  50: '#FEF1F0',
  100: '#FDE0DD',
  200: '#FBC2BD',
  300: '#F79D95',
  400: '#F2766B',
  500: '#E35144',
  600: '#C13A2E',
  700: '#9E2E25',
  800: '#7F2922',
  900: '#67241F',
  950: '#380E0B',
}

const ink = {
  0: '#FFFFFF',
  50: '#F0F4F5',
  100: '#E4ECEE',
  200: '#C9D6DA',
  300: '#9DB0B5',
  400: '#7D9196',
  500: '#647A80',
  600: '#44585E',
  700: '#34454B',
  800: '#243237',
  900: '#1A2529',
  950: '#0E1517',
}

export const ScopePreset = definePreset(Aura, {
  primitive: {
    borderRadius: { none: '0', xs: '2px', sm: '3px', md: '4px', lg: '6px', xl: '8px' },
    // Redefining the three semantic ramps (not adding new ones) keeps every component
    // that says "success / warn / danger" on the Scope palette without per-component work.
    green: cube,
    emerald: cube,
    orange: amber,
    amber,
    red: coral,
  },
  semantic: {
    primary: cube,
    formField: {
      paddingX: '0.6rem',
      paddingY: '0.3rem',
      borderRadius: '{border.radius.md}',
      sm: { fontSize: '0.875rem', paddingX: '0.5rem', paddingY: '0.2rem' },
    },
    colorScheme: {
      dark: {
        surface: ink,
        primary: {
          color: '{primary.400}',
          contrastColor: '#04231A',
          hoverColor: '{primary.300}',
          activeColor: '{primary.200}',
        },
        highlight: {
          background: 'color-mix(in srgb, {primary.400}, transparent 86%)',
          focusBackground: 'color-mix(in srgb, {primary.400}, transparent 78%)',
          color: '{primary.300}',
          focusColor: '{primary.200}',
        },
        formField: {
          background: '{surface.900}',
          disabledBackground: '{surface.800}',
          filledBackground: '{surface.800}',
          borderColor: '{surface.700}',
          hoverBorderColor: '{surface.600}',
          focusBorderColor: '{primary.700}',
          color: '{surface.100}',
          placeholderColor: '{surface.500}',
          iconColor: '{surface.400}',
          shadow: 'none',
        },
        text: {
          color: '{surface.100}',
          hoverColor: '{surface.0}',
          mutedColor: '{surface.300}',
          hoverMutedColor: '{surface.200}',
        },
        content: {
          background: '{surface.950}',
          hoverBackground: '{surface.900}',
          borderColor: '{surface.800}',
        },
        overlay: {
          select: { background: '{surface.900}', borderColor: '{surface.700}' },
          popover: { background: '{surface.900}', borderColor: '{surface.700}' },
          modal: { background: '{surface.900}', borderColor: '{surface.700}' },
        },
        navigation: {
          item: {
            focusBackground: '{surface.800}',
            activeBackground: '{surface.800}',
            color: '{surface.100}',
            focusColor: '{surface.0}',
            activeColor: '{surface.0}',
            icon: { color: '{surface.400}', focusColor: '{surface.300}', activeColor: '{surface.300}' },
          },
        },
      },
    },
  },
  components: {
    button: {
      root: { label: { fontWeight: '500' }, sm: { iconOnlyWidth: '1.75rem' } },
      colorScheme: {
        dark: {
          root: {
            secondary: {
              background: '{surface.900}',
              hoverBackground: '{surface.800}',
              activeBackground: '{surface.700}',
              borderColor: '{surface.700}',
              hoverBorderColor: '{surface.600}',
              activeBorderColor: '{surface.600}',
              color: '{surface.100}',
              hoverColor: '{surface.0}',
              activeColor: '{surface.0}',
            },
          },
          text: {
            // Text buttons are quiet by default: colour is spent on the primary action only.
            primary: { color: '{surface.100}', hoverBackground: '{surface.800}', activeBackground: '{surface.700}' },
            secondary: { color: '{surface.300}', hoverBackground: '{surface.800}', activeBackground: '{surface.700}' },
          },
        },
      },
    },
    tree: {
      root: { padding: '0.25rem 0.25rem', gap: '1px', indent: '0.9rem' },
      node: { padding: '0.2rem 0.4rem', gap: '0.3rem' },
      nodeToggleButton: { size: '1.4rem' },
    },
    datatable: {
      headerCell: { sm: { padding: '0.4rem 0.6rem' } },
      bodyCell: { sm: { padding: '0.3rem 0.6rem' } },
      columnTitle: { fontWeight: '560' },
    },
    tag: { root: { fontSize: '0.75rem', fontWeight: '600', padding: '0.15rem 0.45rem' } },
    message: { root: { borderRadius: '{border.radius.md}' } },
    dialog: { root: { borderRadius: '{border.radius.xl}' } },
    tooltip: { root: { borderRadius: '{border.radius.md}' } },
  },
})
