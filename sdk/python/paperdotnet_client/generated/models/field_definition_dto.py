from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union
from uuid import UUID

if TYPE_CHECKING:
    from .field_search_weight import FieldSearchWeight

@dataclass
class FieldDefinitionDto(AdditionalDataHolder, Parsable):
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The allowMultiple property
    allow_multiple: Optional[bool] = False
    # The required property
    required: Optional[bool] = False
    # The choices property
    choices: Optional[list[str]] = None
    # The currencyCode property
    currency_code: Optional[str] = None
    # The description property
    description: Optional[str] = None
    # The displayName property
    display_name: Optional[str] = None
    # The lookupListId property
    lookup_list_id: Optional[UUID] = None
    # The maxLength property
    max_length: Optional[int] = None
    # The maximum property
    maximum: Optional[float] = None
    # The minimum property
    minimum: Optional[float] = None
    # The name property
    name: Optional[str] = None
    # The search property
    search: Optional[FieldSearchWeight] = None
    # The termSetId property
    term_set_id: Optional[UUID] = None
    # The type property
    type: Optional[str] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> FieldDefinitionDto:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: FieldDefinitionDto
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return FieldDefinitionDto()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        from .field_search_weight import FieldSearchWeight

        from .field_search_weight import FieldSearchWeight

        fields: dict[str, Callable[[Any], None]] = {
            "allowMultiple": lambda n : setattr(self, 'allow_multiple', n.get_bool_value()),
            "choices": lambda n : setattr(self, 'choices', n.get_collection_of_primitive_values(str)),
            "currencyCode": lambda n : setattr(self, 'currency_code', n.get_str_value()),
            "description": lambda n : setattr(self, 'description', n.get_str_value()),
            "displayName": lambda n : setattr(self, 'display_name', n.get_str_value()),
            "lookupListId": lambda n : setattr(self, 'lookup_list_id', n.get_uuid_value()),
            "maxLength": lambda n : setattr(self, 'max_length', n.get_int_value()),
            "maximum": lambda n : setattr(self, 'maximum', n.get_float_value()),
            "minimum": lambda n : setattr(self, 'minimum', n.get_float_value()),
            "name": lambda n : setattr(self, 'name', n.get_str_value()),
            "required": lambda n : setattr(self, 'required', n.get_bool_value()),
            "search": lambda n : setattr(self, 'search', n.get_enum_value(FieldSearchWeight)),
            "termSetId": lambda n : setattr(self, 'term_set_id', n.get_uuid_value()),
            "type": lambda n : setattr(self, 'type', n.get_str_value()),
        }
        return fields
    
    def serialize(self,writer: SerializationWriter) -> None:
        """
        Serializes information the current object
        param writer: Serialization writer to use to serialize this model
        Returns: None
        """
        if writer is None:
            raise TypeError("writer cannot be null.")
        writer.write_bool_value("allowMultiple", self.allow_multiple)
        writer.write_collection_of_primitive_values("choices", self.choices)
        writer.write_str_value("currencyCode", self.currency_code)
        writer.write_str_value("description", self.description)
        writer.write_str_value("displayName", self.display_name)
        writer.write_uuid_value("lookupListId", self.lookup_list_id)
        writer.write_int_value("maxLength", self.max_length)
        writer.write_float_value("maximum", self.maximum)
        writer.write_float_value("minimum", self.minimum)
        writer.write_str_value("name", self.name)
        writer.write_bool_value("required", self.required)
        writer.write_enum_value("search", self.search)
        writer.write_uuid_value("termSetId", self.term_set_id)
        writer.write_str_value("type", self.type)
        writer.write_additional_data_value(self.additional_data)
    

